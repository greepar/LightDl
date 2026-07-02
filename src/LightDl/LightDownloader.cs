using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Text.Json;

namespace LightDl;

/// <summary>
/// Lightweight multi-threaded downloader based on HttpClient. Use one instance per active download.
/// </summary>
public sealed class LightDownloader : IDisposable
{
    private const long MetadataFlushBytes = 4L * 1024 * 1024;

    private readonly LightDownloadConfig _config;
    private readonly HttpClient _http;
    private readonly Lock _metadataLock = new();
    private int _isDownloading;
    private bool _disposed;

    public LightDownloader(LightDownloadConfig? config = null)
    {
        _config = (config ?? new LightDownloadConfig()).Clone();
        NormalizeConfig(_config);

        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = Math.Max(_config.MaxChunkCount, _config.ChunkCount) * 2,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            Proxy = _config.Proxy,
            UseProxy = _config.Proxy != null,
        };

        if (_config.IgnoreSslErrors)
        {
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = delegate { return true; }
            };
        }

        _http = new HttpClient(handler) { Timeout = _config.Timeout };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(_config.UserAgent);
    }

    /// <summary>
    /// Downloads a request and returns the completed file result.
    /// </summary>
    public async Task<LightDownloadResult> DownloadAsync(
        LightDownloadRequest request,
        IProgress<LightDownloadProgress>? progress = null,
        IProgress<LightDownloadFileInfo>? fileInfo = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);

        if (Interlocked.Exchange(ref _isDownloading, 1) == 1)
            throw new InvalidOperationException("LightDownloader does not support concurrent downloads. Create one LightDownloader per active download or use LightDownload.DownloadAsync.");

        try
        {
            return await DownloadCoreAsync(request, progress, fileInfo, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _isDownloading, 0);
        }
    }

    /// <summary>
    /// Downloads a request and returns the completed file result.
    /// </summary>
    public Task<LightDownloadResult> DownloadAsync(LightDownloadRequest request, CancellationToken cancellationToken)
    {
        return DownloadAsync(request, progress: null, fileInfo: null, cancellationToken);
    }

    /// <summary>
    /// Downloads a URL to an exact file path.
    /// </summary>
    public Task<LightDownloadResult> DownloadToFileAsync(
        string url,
        string filePath,
        IProgress<LightDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return DownloadAsync(LightDownloadRequest.ToFile(url, filePath), progress, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Downloads a URL into a directory using the remote file name.
    /// </summary>
    public Task<LightDownloadResult> DownloadToDirectoryAsync(
        string url,
        string directoryPath,
        IProgress<LightDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return DownloadAsync(LightDownloadRequest.ToDirectory(url, directoryPath), progress, cancellationToken: cancellationToken);
    }

    private async Task<LightDownloadResult> DownloadCoreAsync(
        LightDownloadRequest request,
        IProgress<LightDownloadProgress>? progress,
        IProgress<LightDownloadFileInfo>? fileInfo,
        CancellationToken ct)
    {
        var url = request.Url;
        var headers = request.Headers;
        var info = await ProbeFileInfoAsync(url, headers, ct).ConfigureAwait(false);
        fileInfo?.Report(info);
        request.FileInfoHandler?.Invoke(info);
        Action<LightDownloadProgress>? progressChanged = progress is null && request.ProgressHandler is null
            ? null
            : value =>
            {
                progress?.Report(value);
                request.ProgressHandler?.Invoke(value);
            };
        var totalLength = info.Size;
        var destinationPath = ResolveDestinationPath(request.DestinationPath, info.FileName, request.DestinationKind);
        if (TryHandleExistingFile(info, ref destinationPath, out var skippedResult))
        {
            progressChanged?.Invoke(new LightDownloadProgress
            {
                DownloadedBytes = skippedResult.Size,
                TotalBytes = skippedResult.Size,
                Speed = 0
            });
            return skippedResult;
        }

        if (!info.SupportsRange)
        {
            try
            {
                await DownloadSingleThreadAsync(url, destinationPath, totalLength, headers, progressChanged, ct).ConfigureAwait(false);
                return CreateDownloadResult(info, destinationPath);
            }
            catch
            {
                if (!_config.EnableResume)
                    DeleteIfExists(destinationPath);

                throw;
            }
        }

        var tempPath = _config.EnableResume ? destinationPath + _config.TempFileExtension : destinationPath;
        var metadataPath = destinationPath + _config.MetadataFileExtension;
        try
        {
            var metadata = LoadOrCreateMetadata(url, totalLength, tempPath, metadataPath);
            var completedRanges = MergeRanges(metadata.CompletedRanges.Select(r => new DownloadRange(r.Start, r.End)).ToList());

            Preallocate(tempPath, totalLength);

            var allocator = new RangeAllocator(BuildMissingRanges(totalLength, completedRanges));
            var retryQueue = new ConcurrentQueue<DownloadSegment>();
            var activeSegments = 0;
            var downloaded = new AtomicLong(completedRanges.Sum(r => r.End - r.Start + 1));
            var currentConcurrency = Math.Min(_config.ChunkCount, _config.MaxChunkCount);
            var currentSegmentSize = CalculateStableSegmentSize(totalLength, currentConcurrency);
            var stopwatch = Stopwatch.StartNew();

            using var progressCts = new CancellationTokenSource();
            var progressTask = ReportProgressAndAdaptLoop(
                progressCts.Token,
                downloaded.Read,
                totalLength,
                progressChanged,
                () => Volatile.Read(ref currentConcurrency),
                value => Volatile.Write(ref currentConcurrency, value),
                () => Interlocked.Read(ref currentSegmentSize),
                value => Interlocked.Exchange(ref currentSegmentSize, value));

            var workerCount = _config.EnableDynamicConcurrency ? _config.MaxChunkCount : _config.ChunkCount;
            var workers = new Task[workerCount];
            for (var workerIndex = 0; workerIndex < workerCount; workerIndex++)
            {
                var index = workerIndex;
                workers[index] = Task.Run(async () =>
                {
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();

                        if (index >= Volatile.Read(ref currentConcurrency))
                        {
                            if (allocator.IsEmpty && retryQueue.IsEmpty && Volatile.Read(ref activeSegments) == 0)
                                break;

                            await Task.Delay(100, ct).ConfigureAwait(false);
                            continue;
                        }

                        if (!retryQueue.TryDequeue(out var segment) && !allocator.TryRent(Interlocked.Read(ref currentSegmentSize), out segment))
                        {
                            if (Volatile.Read(ref activeSegments) == 0)
                                break;

                            await Task.Delay(100, ct).ConfigureAwait(false);
                            continue;
                        }

                        Interlocked.Increment(ref activeSegments);
                        try
                        {
                            await DownloadChunkAsync(url, tempPath, segment.Start, segment.End,
                                downloaded.Add,
                                downloaded.Read, totalLength, stopwatch, headers,
                                (rangeStart, rangeEnd) => AddCompletedRange(metadata, metadataPath, rangeStart, rangeEnd),
                                ct).ConfigureAwait(false);
                        }
                        catch (SegmentRetryException ex) when (segment.RetryCount < _config.MaxRetry)
                        {
                            if (ex.NextStart > segment.Start)
                                AddCompletedRange(metadata, metadataPath, segment.Start, Math.Min(ex.NextStart - 1, segment.End));

                            if (ex.NextStart <= segment.End)
                                retryQueue.Enqueue(new DownloadSegment(ex.NextStart, segment.End, segment.RetryCount + 1));

                            await Task.Delay(500 * (segment.RetryCount + 1), ct).ConfigureAwait(false);
                        }
                        catch (Exception) when (segment.RetryCount < _config.MaxRetry)
                        {
                            retryQueue.Enqueue(new DownloadSegment(segment.Start, segment.End, segment.RetryCount + 1));
                            await Task.Delay(500 * (segment.RetryCount + 1), ct).ConfigureAwait(false);
                        }
                        finally
                        {
                            Interlocked.Decrement(ref activeSegments);
                        }
                    }
                }, ct);
            }

            await Task.WhenAll(workers).ConfigureAwait(false);

            await progressCts.CancelAsync();
            try { await progressTask.ConfigureAwait(false); } catch (OperationCanceledException) { }

            stopwatch.Stop();
            if (!_config.EnableResume) return CreateDownloadResult(info, destinationPath);
            if (File.Exists(destinationPath))
                File.Delete(destinationPath);

            File.Move(tempPath, destinationPath);
            if (File.Exists(metadataPath))
                File.Delete(metadataPath);

            return CreateDownloadResult(info, destinationPath);
        }
        catch
        {
            if (!_config.EnableResume)
                DeletePartialFiles(destinationPath, tempPath, metadataPath);

            throw;
        }
    }

    private async Task<LightDownloadFileInfo> ProbeFileInfoAsync(string url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        ApplyHeaders(request, headers);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var supportsRange = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
        long size;
        if (supportsRange && response.Content.Headers.ContentRange is { Length: { } len })
        {
            size = len;
        }
        else if (response.Content.Headers.ContentLength is { } cl)
        {
            size = cl;
        }
        else
        {
            throw new InvalidOperationException("The server did not return the file size, so chunked download cannot continue.");
        }

        return new LightDownloadFileInfo
        {
            FileName = GetFileName(response.RequestMessage?.RequestUri?.ToString() ?? url, response),
            Size = size,
            ContentType = response.Content.Headers.ContentType?.ToString(),
            SupportsRange = supportsRange,
        };
    }

    private async Task DownloadChunkAsync(
        string url,
        string path,
        long start,
        long end,
        Action<long> onBytesReceived,
        Func<long> getGlobalDownloaded,
        long totalLength,
        Stopwatch globalStopwatch,
        IReadOnlyDictionary<string, string>? headers,
        Action<long, long> onRangeCompleted,
        CancellationToken ct)
    {
        var currentOffset = start;
        var lastCommittedOffset = start;
        var segmentBytes = 0L;
        var segmentStopwatch = Stopwatch.StartNew();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(start, end);
            ApplyHeaders(request, headers);

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode != System.Net.HttpStatusCode.PartialContent)
                throw new SegmentRetryException(start, $"The chunk request did not return 206 Partial Content (actual: {response.StatusCode}). Range requests may not be supported.");

            response.EnsureSuccessStatusCode();
            ValidateContentRange(response.Content.Headers.ContentRange, start, end, totalLength);

            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dest = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Write,
                _config.BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

            dest.Seek(start, SeekOrigin.Begin);

            var buffer = new byte[_config.BufferSize];
            while (true)
            {
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readCts.CancelAfter(_config.NoDataTimeout);

                int read;
                try
                {
                    read = await source.ReadAsync(buffer, readCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new SegmentRetryException(currentOffset, "The connection received no data for too long and will be requeued.");
                }

                if (read == 0)
                    break;

                await dest.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                currentOffset += read;
                segmentBytes += read;
                onBytesReceived(read);

                if (currentOffset - lastCommittedOffset >= MetadataFlushBytes)
                {
                    onRangeCompleted(lastCommittedOffset, currentOffset - 1);
                    lastCommittedOffset = currentOffset;
                }

                await ApplySpeedLimitAsync(getGlobalDownloaded(), globalStopwatch, ct).ConfigureAwait(false);

                var remainingBytes = end - currentOffset + 1;
                if (remainingBytes >= _config.MinRemainingBytesForRequeue && IsSlowSegment(segmentBytes, segmentStopwatch, getGlobalDownloaded, globalStopwatch))
                    throw new SegmentRetryException(currentOffset, "The connection is much slower than the global average and will be requeued.");
            }

            if (currentOffset > lastCommittedOffset)
                onRangeCompleted(lastCommittedOffset, currentOffset - 1);
        }
        catch (SegmentRetryException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SegmentRetryException(currentOffset, "The segment download failed and will be requeued.", ex);
        }
    }

    private bool IsSlowSegment(long segmentBytes, Stopwatch segmentStopwatch, Func<long> getGlobalDownloaded, Stopwatch globalStopwatch)
    {
        if (segmentStopwatch.Elapsed < _config.SlowSegmentMinDuration)
            return false;

        var globalSeconds = globalStopwatch.Elapsed.TotalSeconds;
        var segmentSeconds = segmentStopwatch.Elapsed.TotalSeconds;
        if (globalSeconds <= 0 || segmentSeconds <= 0)
            return false;

        var globalSpeed = getGlobalDownloaded() / globalSeconds;
        var segmentSpeed = segmentBytes / segmentSeconds;
        return globalSpeed > 0 && segmentSpeed < globalSpeed * _config.SlowSpeedRatio;
    }

    private async Task DownloadSingleThreadAsync(string url, string path, long totalLength, IReadOnlyDictionary<string, string>? headers, Action<LightDownloadProgress>? progressChanged, CancellationToken ct)
    {
        var downloaded = new AtomicLong();
        var sw = Stopwatch.StartNew();

        using var progressCts = new CancellationTokenSource();
        var progressTask = ReportProgressOnlyLoop(progressCts.Token, downloaded.Read, totalLength, progressChanged);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyHeaders(request, headers);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dest = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
            _config.BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[_config.BufferSize];
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dest.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            downloaded.Add(read);
            await ApplySpeedLimitAsync(downloaded.Read(), sw, ct).ConfigureAwait(false);
        }

        await progressCts.CancelAsync();
        try { await progressTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        sw.Stop();
    }

    private async Task ReportProgressOnlyLoop(CancellationToken ct, Func<long> getDownloaded, long total, Action<LightDownloadProgress>? progressChanged)
    {
        var lastBytes = getDownloaded();
        var lastTime = Stopwatch.GetTimestamp();
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_config.ProgressIntervalMs, ct).ConfigureAwait(false);
            var nowBytes = getDownloaded();
            var nowTime = Stopwatch.GetTimestamp();
            var seconds = (nowTime - lastTime) / (double)Stopwatch.Frequency;
            var speed = seconds > 0 ? (nowBytes - lastBytes) / seconds : 0;
            lastBytes = nowBytes;
            lastTime = nowTime;

            var progress = new LightDownloadProgress
            {
                DownloadedBytes = nowBytes,
                TotalBytes = total,
                Speed = speed,
            };
            progressChanged?.Invoke(progress);
        }
    }

    private async Task ReportProgressAndAdaptLoop(
        CancellationToken ct,
        Func<long> getDownloaded,
        long total,
        Action<LightDownloadProgress>? progressChanged,
        Func<int> getConcurrency,
        Action<int> setConcurrency,
        Func<long> getSegmentSize,
        Action<long> setSegmentSize)
    {
        var lastBytes = getDownloaded();
        var lastTime = Stopwatch.GetTimestamp();
        var lastAdapt = DateTimeOffset.UtcNow;
        var previousSpeed = 0d;

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_config.ProgressIntervalMs, ct).ConfigureAwait(false);
            var nowBytes = getDownloaded();
            var nowTime = Stopwatch.GetTimestamp();
            var seconds = (nowTime - lastTime) / (double)Stopwatch.Frequency;
            var speed = seconds > 0 ? (nowBytes - lastBytes) / seconds : 0;

            var progress = new LightDownloadProgress
            {
                DownloadedBytes = nowBytes,
                TotalBytes = total,
                Speed = speed,
            };
            progressChanged?.Invoke(progress);

            if (DateTimeOffset.UtcNow - lastAdapt >= _config.AdaptInterval)
            {
                AdaptDownloadParameters(speed, previousSpeed, getConcurrency, setConcurrency, getSegmentSize, setSegmentSize);
                previousSpeed = speed;
                lastAdapt = DateTimeOffset.UtcNow;
            }

            lastBytes = nowBytes;
            lastTime = nowTime;
        }
    }

    private void AdaptDownloadParameters(
        double speed,
        double previousSpeed,
        Func<int> getConcurrency,
        Action<int> setConcurrency,
        Func<long> getSegmentSize,
        Action<long> setSegmentSize)
    {
        if (previousSpeed <= 0 || speed <= 0)
            return;

        var concurrency = getConcurrency();
        var segmentSize = getSegmentSize();

        if (speed > previousSpeed * 1.08)
        {
            if (_config.EnableDynamicConcurrency && concurrency < _config.MaxChunkCount)
                setConcurrency(concurrency + 1);

            if (_config.EnableDynamicSegmentSize && segmentSize < _config.MaxSegmentSize)
                setSegmentSize(Math.Min(segmentSize * 2, _config.MaxSegmentSize));
        }
        else if (speed < previousSpeed * 0.85)
        {
            if (_config.EnableDynamicConcurrency && concurrency < _config.MaxChunkCount)
                setConcurrency(concurrency + 1);

            if (_config.EnableDynamicSegmentSize && segmentSize > _config.MinSegmentSize)
                setSegmentSize(Math.Max(segmentSize / 2, _config.MinSegmentSize));
        }
        else if (_config.EnableDynamicConcurrency && concurrency > _config.MinChunkCount)
        {
            setConcurrency(concurrency - 1);
        }
    }

    private async Task ApplySpeedLimitAsync(long downloadedBytes, Stopwatch stopwatch, CancellationToken ct)
    {
        var limit = _config.SpeedLimitProvider?.Invoke();
        if (limit is null or <= 0 || downloadedBytes <= 0)
            return;

        var expectedSeconds = downloadedBytes / limit.Value;
        var delay = expectedSeconds - stopwatch.Elapsed.TotalSeconds;
        if (delay > 0.01)
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(delay, 1)), ct).ConfigureAwait(false);
    }

    private long CalculateStableSegmentSize(long totalLength, int concurrency)
    {
        var targetSegments = Math.Max(concurrency, 1);
        var sizeForConcurrency = (long)Math.Ceiling(totalLength / (double)targetSegments);

        // Large files keep the configured big segment for stable throughput.
        // Small files/audio streams are split into enough ranges to avoid single slow CDN connection.
        return Math.Clamp(
            Math.Min(_config.SegmentSize, sizeForConcurrency),
            _config.BufferSize,
            _config.MaxSegmentSize);
    }

    private DownloadMetadata LoadOrCreateMetadata(string url, long totalLength, string tempPath, string metadataPath)
    {
        if (!_config.EnableResume || !File.Exists(tempPath) || !File.Exists(metadataPath))
            return new DownloadMetadata(url, totalLength, []);

        try
        {
            var json = File.ReadAllText(metadataPath);
            var metadata = JsonSerializer.Deserialize(json, LightDlJsonContext.Default.DownloadMetadata);
            if (metadata?.Url == url && metadata.TotalLength == totalLength)
                return metadata;
        }
        catch
        {
            // Broken metadata means a fresh download is safer.
        }

        File.Delete(tempPath);
        File.Delete(metadataPath);
        return new DownloadMetadata(url, totalLength, []);
    }

    private void AddCompletedRange(DownloadMetadata metadata, string metadataPath, long start, long end)
    {
        if (end < start)
            return;

        lock (_metadataLock)
        {
            metadata.CompletedRanges.Add(new CompletedRange(start, end));
            metadata.CompletedRanges = MergeRanges(metadata.CompletedRanges.Select(r => new DownloadRange(r.Start, r.End)).ToList())
                .Select(r => new CompletedRange(r.Start, r.End))
                .ToList();

            if (_config.EnableResume)
                SaveMetadata(metadata, metadataPath);
        }
    }

    private static void SaveMetadata(DownloadMetadata metadata, string metadataPath)
    {
        var tempPath = metadataPath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(metadata, LightDlJsonContext.Default.DownloadMetadata));
        File.SetAttributes(tempPath, FileAttributes.Hidden);

        if (File.Exists(metadataPath))
            File.Replace(tempPath, metadataPath, null);
        else
            File.Move(tempPath, metadataPath);
    }

    private static void Preallocate(string path, long totalLength)
    {
        using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Write, 4096, FileOptions.None);
        fs.SetLength(totalLength);
    }

    private static void DeletePartialFiles(string destinationPath, string tempPath, string metadataPath)
    {
        DeleteIfExists(tempPath);
        DeleteIfExists(metadataPath);

        if (string.Equals(tempPath, destinationPath, StringComparison.OrdinalIgnoreCase))
            DeleteIfExists(destinationPath);
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Cleanup is best-effort. Preserve the original download exception.
        }
    }

    private static List<DownloadRange> BuildMissingRanges(long totalLength, List<DownloadRange> completedRanges)
    {
        var missing = new List<DownloadRange>();
        var cursor = 0L;
        foreach (var range in completedRanges)
        {
            if (range.Start > cursor)
                missing.Add(new DownloadRange(cursor, range.Start - 1));

            cursor = Math.Max(cursor, range.End + 1);
        }

        if (cursor <= totalLength - 1)
            missing.Add(new DownloadRange(cursor, totalLength - 1));

        return missing;
    }

    private static List<DownloadRange> MergeRanges(List<DownloadRange> ranges)
    {
        if (ranges.Count == 0)
            return ranges;

        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<DownloadRange> { ranges[0] };
        for (var i = 1; i < ranges.Count; i++)
        {
            var last = merged[^1];
            var current = ranges[i];
            if (current.Start <= last.End + 1)
            {
                merged[^1] = new DownloadRange(last.Start, Math.Max(last.End, current.End));
            }
            else
            {
                merged.Add(current);
            }
        }

        return merged;
    }

    private static void ValidateContentRange(ContentRangeHeaderValue? contentRange, long start, long end, long totalLength)
    {
        if (contentRange?.From != start || contentRange.To != end || contentRange.Length != totalLength)
            throw new SegmentRetryException(start, "The server returned a Content-Range that does not match the requested range.");
    }

    private static void ApplyHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null)
            return;

        foreach (var (name, value) in headers)
        {
            request.Headers.Remove(name);
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }

    private static string GetFileName(string url, HttpResponseMessage response)
    {
        var contentDisposition = response.Content.Headers.ContentDisposition;
        var fileName = contentDisposition?.FileNameStar ?? contentDisposition?.FileName;
        if (!string.IsNullOrWhiteSpace(fileName))
            return SanitizeFileName(fileName.Trim('"'));

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var pathFileName = Path.GetFileName(uri.LocalPath);
            if (!string.IsNullOrWhiteSpace(pathFileName))
                return SanitizeFileName(Uri.UnescapeDataString(pathFileName));
        }

        return "download";
    }

    private bool TryHandleExistingFile(LightDownloadFileInfo info, ref string destinationPath, out LightDownloadResult result)
    {
        result = null!;
        if (!File.Exists(destinationPath))
            return false;

        switch (_config.FileConflictPolicy)
        {
            case LightDownloadFileConflictPolicy.Overwrite:
                return false;

            case LightDownloadFileConflictPolicy.Fail:
                throw new IOException($"The destination file already exists: {destinationPath}");

            case LightDownloadFileConflictPolicy.Skip:
                result = CreateDownloadResult(info, destinationPath, skipped: true, size: new FileInfo(destinationPath).Length);
                return true;

            case LightDownloadFileConflictPolicy.Rename:
                destinationPath = GetUniqueFilePath(destinationPath);
                return false;

            default:
                throw new ArgumentOutOfRangeException(nameof(_config.FileConflictPolicy), _config.FileConflictPolicy, "Unknown file conflict policy.");
        }
    }

    private static string ResolveDestinationPath(string path, string fileName, LightDownloadDestinationKind destinationKind)
    {
        switch (destinationKind)
        {
            case LightDownloadDestinationKind.File:
                if (Directory.Exists(path))
                    throw new IOException($"The destination file path points to an existing directory: {path}");

                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                return path;

            case LightDownloadDestinationKind.Directory:
                Directory.CreateDirectory(path);
                return Path.Combine(path, fileName);

            default:
                throw new ArgumentOutOfRangeException(nameof(destinationKind), destinationKind, "Unknown destination kind.");
        }
    }

    private static string GetUniqueFilePath(string path)
    {
        var directory = Path.GetDirectoryName(path);
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var i = 1; ; i++)
        {
            var candidateName = string.IsNullOrEmpty(extension) ? $"{name} ({i})" : $"{name} ({i}){extension}";
            var candidate = string.IsNullOrWhiteSpace(directory) ? candidateName : Path.Combine(directory, candidateName);
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    private static LightDownloadResult CreateDownloadResult(LightDownloadFileInfo info, string destinationPath, bool skipped = false, long? size = null)
    {
        return new LightDownloadResult
        {
            FileName = Path.GetFileName(destinationPath),
            FilePath = destinationPath,
            Size = size ?? info.Size,
            ContentType = info.ContentType,
            SupportsRange = info.SupportsRange,
            Skipped = skipped
        };
    }

    private static string SanitizeFileName(string fileName)
    {
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(invalidChar, '_');

        fileName = fileName.Trim();
        return string.IsNullOrWhiteSpace(fileName) ? "download" : fileName;
    }

    private static void NormalizeConfig(LightDownloadConfig config)
    {
        config.BufferSize = Math.Max(config.BufferSize, 8 * 1024);
        config.MinChunkCount = Math.Max(config.MinChunkCount, 1);
        config.ChunkCount = Math.Max(config.ChunkCount, config.MinChunkCount);
        config.MaxChunkCount = Math.Max(config.MaxChunkCount, config.ChunkCount);
        config.MinSegmentSize = Math.Max(config.MinSegmentSize, config.BufferSize);
        config.SegmentSize = Math.Clamp(config.SegmentSize, config.MinSegmentSize, config.MaxSegmentSize);
        config.MaxSegmentSize = Math.Max(config.MaxSegmentSize, config.SegmentSize);
        config.ProgressIntervalMs = Math.Max(config.ProgressIntervalMs, 100);
        config.MaxRetry = Math.Max(config.MaxRetry, 0);
    }

    private sealed class RangeAllocator(IEnumerable<DownloadRange> ranges)
    {
        private readonly Lock _lock = new();
        private readonly Queue<DownloadRange> _ranges = new(ranges);

        public bool IsEmpty
        {
            get
            {
                lock (_lock)
                    return _ranges.Count == 0;
            }
        }

        public bool TryRent(long size, out DownloadSegment segment)
        {
            lock (_lock)
            {
                if (_ranges.Count == 0)
                {
                    segment = default;
                    return false;
                }

                var range = _ranges.Dequeue();
                var end = Math.Min(range.End, range.Start + size - 1);
                if (end < range.End)
                    _ranges.Enqueue(new DownloadRange(end + 1, range.End));

                segment = new DownloadSegment(range.Start, end, 0);
                return true;
            }
        }
    }

    private readonly record struct DownloadRange(long Start, long End);

    private readonly record struct DownloadSegment(long Start, long End, int RetryCount);

    private sealed class AtomicLong(long value = 0)
    {
        private long _value = value;

        public long Read() => Interlocked.Read(ref _value);

        public void Add(long value) => Interlocked.Add(ref _value, value);
    }

    internal sealed class CompletedRange(long start, long end)
    {
        public long Start { get; set; } = start;

        public long End { get; set; } = end;
    }

    internal sealed class DownloadMetadata(string url, long totalLength, List<CompletedRange> completedRanges)
    {
        public string Url { get; init; } = url;

        public long TotalLength { get; init; } = totalLength;

        public List<CompletedRange> CompletedRanges { get; set; } = completedRanges;
    }

    private sealed class SegmentRetryException(long nextStart, string message, Exception? innerException = null)
        : Exception(message, innerException)
    {
        public long NextStart { get; } = nextStart;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}
