using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LightDl;

/// <summary>
/// Lightweight multi-threaded downloader based on HttpClient. Use one instance per active download.
/// </summary>
public sealed class LightDownloader : IDisposable
{
    /// <summary>Reported as <see cref="LightDownloadFileInfo.Size" /> when the server does not declare a length.</summary>
    public const long UnknownSize = -1;

    private const long MetadataFlushBytes = 4L * 1024 * 1024;
    private const int MaxRedirects = 10;
    private const int MaxFileNameBytes = 255;
    private const string DefaultFileName = "download";
    private static readonly TimeSpan MinIdleStallTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Ceiling on the staggered connection ramp, so a large worker count still starts promptly.</summary>
    private static readonly TimeSpan MaxRampUpDelay = TimeSpan.FromSeconds(2);

    /// <summary>Challenge pages are a few KB of HTML; a real download that small is not worth guarding.</summary>
    private const long MaxChallengePageBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly LightDownloadConfig _config;
    private readonly HttpClient _http;
    private readonly Lock _metadataLock = new();
    private readonly Lock _metadataFileLock = new();
    private long _metadataVersion;
    private long _metadataWrittenVersion;
    private long _lastMetadataSaveTimestamp;
    private int _isDownloading;
    private int _disposed;

    public LightDownloader(LightDownloadConfig? config = null)
    {
        _config = (config ?? new LightDownloadConfig()).Clone();
        NormalizeConfig(_config);

        HttpMessageHandler handler;
        if (_config.HttpMessageHandlerFactory is not null)
        {
            handler = _config.HttpMessageHandlerFactory();
            ValidateHandler(handler);
        }
        else
        {
            var socketsHandler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                // Redirects and the Cookie header are handled here; letting the handler add its own
                // cookies on top would duplicate or override caller-supplied credentials.
                UseCookies = false,
                ConnectTimeout = _config.ConnectTimeout,
                MaxConnectionsPerServer = Math.Max(_config.MaxChunkCount, _config.ChunkCount) * 2,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                Proxy = _config.Proxy,
                UseProxy = _config.UseProxy,
            };

            if (_config.IgnoreSslErrors)
            {
                socketsHandler.SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = delegate { return true; }
                };
            }

            handler = socketsHandler;
        }

        _http = new HttpClient(handler) { Timeout = _config.Timeout };
        if (!_http.DefaultRequestHeaders.UserAgent.TryParseAdd(_config.UserAgent))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", _config.UserAgent);
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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        ArgumentNullException.ThrowIfNull(request);

        if (Interlocked.Exchange(ref _isDownloading, 1) == 1)
            throw new InvalidOperationException(
                "LightDownloader does not support concurrent downloads. Create one LightDownloader per active download.");

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
        return DownloadAsync(LightDownloadRequest.ToFile(url, filePath), progress,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Downloads a URI to an exact file path.
    /// </summary>
    public Task<LightDownloadResult> DownloadToFileAsync(
        Uri url,
        string filePath,
        IProgress<LightDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return DownloadAsync(LightDownloadRequest.ToFile(url, filePath), progress,
            cancellationToken: cancellationToken);
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
        return DownloadAsync(LightDownloadRequest.ToDirectory(url, directoryPath), progress,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Downloads a URI into a directory using the remote file name.
    /// </summary>
    public Task<LightDownloadResult> DownloadToDirectoryAsync(
        Uri url,
        string directoryPath,
        IProgress<LightDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return DownloadAsync(LightDownloadRequest.ToDirectory(url, directoryPath), progress,
            cancellationToken: cancellationToken);
    }

    private async Task<LightDownloadResult> DownloadCoreAsync(
        LightDownloadRequest request,
        IProgress<LightDownloadProgress>? progress,
        IProgress<LightDownloadFileInfo>? fileInfo,
        CancellationToken ct)
    {
        var url = request.RequestUri;
        var urlString = request.Url;
        var headers = request.Headers;
        var probe = await ProbeFileInfoAsync(url, headers, ct).ConfigureAwait(false);
        var downloadTarget = new DownloadTarget(url, probe.DownloadUri, headers);
        var progressChanged = BuildProgressReporter(progress, request);

        var totalLength = probe.Size;
        var destinationPath = ResolveDestinationPath(request.DestinationPath, probe.FileName, request.DestinationKind);

        var info = probe.CreateFileInfo(destinationPath);
        SafeInvoke(() => fileInfo?.Report(info));
        SafeInvoke(() => request.FileInfoHandler?.Invoke(info));

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

        // Every path writes to a temporary file and is renamed on success, so a failed download can
        // never leave a truncated file sitting at the destination name.
        var ranged = info.SupportsRange && totalLength >= 0;
        var tempPath = destinationPath + _config.TempFileExtension;
        var metadataPath = ResolveMetadataPath(destinationPath);

        try
        {
            long finalSize;
            long transferredBytes;
            var transferStopwatch = Stopwatch.StartNew();
            if (ranged)
            {
                transferredBytes = await DownloadRangedAsync(downloadTarget, tempPath, metadataPath, urlString,
                    totalLength, probe, progressChanged, ct).ConfigureAwait(false);
                finalSize = totalLength;
            }
            else
            {
                finalSize = await DownloadSingleStreamAsync(downloadTarget, tempPath, totalLength, progressChanged, ct)
                    .ConfigureAwait(false);
                transferredBytes = finalSize;
            }

            transferStopwatch.Stop();

            await VerifyChecksumAsync(tempPath, ct).ConfigureAwait(false);

            File.Move(tempPath, destinationPath, overwrite: true);
            DeleteIfExists(metadataPath);

            // Reporting 0 at 100% reads as "stalled". The average over what this run actually
            // transferred is both truthful and the number a caller wants to show.
            var transferSeconds = transferStopwatch.Elapsed.TotalSeconds;
            progressChanged?.Invoke(new LightDownloadProgress
            {
                DownloadedBytes = finalSize,
                TotalBytes = finalSize,
                Speed = transferSeconds > 0 ? transferredBytes / transferSeconds : 0
            });
            return CreateDownloadResult(info, destinationPath, size: finalSize);
        }
        catch (Exception ex)
        {
            // Partial data is only worth keeping when it can actually be resumed and is still valid.
            var resumable = ranged && _config.EnableResume && !IsPartialDataInvalid(ex);
            if (!resumable)
            {
                DeleteIfExists(tempPath);
                DeleteIfExists(metadataPath);
            }

            throw Rethrow(ex);
        }
    }

    private async Task<long> DownloadRangedAsync(
        DownloadTarget downloadTarget,
        string tempPath,
        string metadataPath,
        string urlString,
        long totalLength,
        ProbeResult probe,
        Action<LightDownloadProgress>? progressChanged,
        CancellationToken ct)
    {
        var metadata = LoadOrCreateMetadata(urlString, totalLength, probe, tempPath, metadataPath);
        metadata.CompletedRanges = MergeRanges(metadata.CompletedRanges);
        var completedBytes = metadata.CompletedRanges.Sum(r => r.End - r.Start + 1);

        EnsureFreeSpace(tempPath, totalLength - completedBytes);
        Preallocate(tempPath, totalLength);

        var allocator = new RangeAllocator(BuildMissingRanges(totalLength, metadata.CompletedRanges));
        var retryQueue = new ConcurrentQueue<DownloadSegment>();
        var activeSegments = 0;
        var downloaded = new AtomicLong(completedBytes);
        var sessionDownloaded = new AtomicLong();
        var currentConcurrency = Math.Min(_config.ChunkCount, _config.MaxChunkCount);
        var currentSegmentSize = CalculateStableSegmentSize(totalLength, currentConcurrency);
        // Throttle recovery may only climb back to what the caller actually asked for. Without
        // dynamic concurrency that is ChunkCount, and no worker exists above it to use the slot.
        var maxConcurrency = _config.EnableDynamicConcurrency ? _config.MaxChunkCount : currentConcurrency;
        var stopwatch = Stopwatch.StartNew();
        var failure = new FailureState();
        var throttle = _config.EnableThrottleBackoff
            ? new ThrottleController(
                currentConcurrency,
                Math.Min(_config.MinChunkCount, currentConcurrency),
                _config.ThrottleBackoffDelay,
                _config.ThrottleRecoveryInterval,
                () => Volatile.Read(ref currentConcurrency),
                value => Volatile.Write(ref currentConcurrency, value))
            : null;

        // A segment that fails for good must stop the whole download immediately: without this the
        // remaining workers keep pulling bytes for a download that is already doomed.
        using var failureCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var linkedCt = failureCts.Token;

        using var progressCts = new CancellationTokenSource();
        var progressTask = ReportProgressAndAdaptLoop(
            progressCts.Token,
            downloaded.Read,
            totalLength,
            progressChanged,
            () => Volatile.Read(ref currentConcurrency),
            value => Volatile.Write(ref currentConcurrency, value),
            () => Interlocked.Read(ref currentSegmentSize),
            value => Interlocked.Exchange(ref currentSegmentSize, value),
            throttle,
            maxConcurrency,
            stalledFor =>
            {
                if (failure.TrySet(new LightDownloadException(
                        $"The download stalled: no data arrived on any connection for {stalledFor.TotalSeconds:0}s.")))
                    failureCts.Cancel();
            });

        var workerCount = _config.EnableDynamicConcurrency ? _config.MaxChunkCount : _config.ChunkCount;
        var workers = new Task[workerCount];
        for (var workerIndex = 0; workerIndex < workerCount; workerIndex++)
        {
            var index = workerIndex;
            workers[index] = Task.Run(async () =>
            {
                // Opening every connection in the same instant is what per-IP rate limiters and
                // anti-bot gates react to. Staggering the first request costs a second on a long
                // transfer and nothing on a short one, where the early workers finish the job.
                if (index > 0 && _config.ConnectionRampUpDelay > TimeSpan.Zero)
                {
                    var ramp = Math.Min(
                        index * _config.ConnectionRampUpDelay.TotalMilliseconds,
                        MaxRampUpDelay.TotalMilliseconds);
                    await Task.Delay(TimeSpan.FromMilliseconds(ramp), linkedCt).ConfigureAwait(false);
                }

                while (true)
                {
                    linkedCt.ThrowIfCancellationRequested();

                    if (throttle is not null)
                        await throttle.WaitAsync(linkedCt).ConfigureAwait(false);

                    if (index >= Volatile.Read(ref currentConcurrency))
                    {
                        if (allocator.IsEmpty && retryQueue.IsEmpty && Volatile.Read(ref activeSegments) == 0)
                            break;

                        await Task.Delay(100, linkedCt).ConfigureAwait(false);
                        continue;
                    }

                    if (!retryQueue.TryDequeue(out var segment) &&
                        !allocator.TryRent(Interlocked.Read(ref currentSegmentSize), out segment))
                    {
                        if (Volatile.Read(ref activeSegments) == 0)
                            break;

                        await Task.Delay(100, linkedCt).ConfigureAwait(false);
                        continue;
                    }

                    Interlocked.Increment(ref activeSegments);
                    try
                    {
                        await DownloadChunkAsync(downloadTarget, tempPath, segment.Start, segment.End,
                            bytes =>
                            {
                                downloaded.Add(bytes);
                                sessionDownloaded.Add(bytes);
                            },
                            sessionDownloaded.Read,
                            () => Volatile.Read(ref activeSegments),
                            () => GetStallTimeout(Volatile.Read(ref activeSegments),
                                Volatile.Read(ref currentConcurrency)),
                            totalLength, stopwatch, probe.IfRange, probe.ContentType,
                            (rangeStart, rangeEnd) => AddCompletedRange(metadata, metadataPath, rangeStart, rangeEnd),
                            linkedCt).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (linkedCt.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        var nextStart = ex is SegmentRetryException retry ? retry.NextStart : segment.Start;
                        if (nextStart > segment.Start)
                            AddCompletedRange(metadata, metadataPath, segment.Start,
                                Math.Min(nextStart - 1, segment.End));

                        if (!IsRetryable(ex) || segment.RetryCount >= _config.MaxRetry)
                        {
                            if (failure.TrySet(ex))
                                await failureCts.CancelAsync();

                            throw;
                        }

                        if (nextStart <= segment.End)
                            retryQueue.Enqueue(new DownloadSegment(nextStart, segment.End, segment.RetryCount + 1));

                        if (ex is SegmentRetryException { Throttled: true } throttled)
                            throttle?.Trip(throttled.RetryAfter);

                        var delay = GetRetryDelay(segment.RetryCount, (ex as SegmentRetryException)?.RetryAfter);
                        NotifyRetry(segment.Start, segment.End, segment.RetryCount + 1, delay, ex);
                        await Task.Delay(delay, linkedCt).ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref activeSegments);
                    }
                }
            }, linkedCt);
        }

        try
        {
            try
            {
                await Task.WhenAll(workers).ConfigureAwait(false);
            }
            catch
            {
                // A worker's fatal error is the real cause; every other worker just observed the
                // cancellation it triggered.
                failure.ThrowIfFailed();
                throw;
            }
            finally
            {
                await StopProgressReportingAsync(progressCts, progressTask).ConfigureAwait(false);
                stopwatch.Stop();
            }

            EnsureAllRangesComplete(metadata, totalLength);
        }
        catch
        {
            ForceSaveMetadata(metadata, metadataPath);
            throw;
        }

        return sessionDownloaded.Read();
    }

    private async Task<long> DownloadSingleStreamAsync(
        DownloadTarget target,
        string tempPath,
        long totalLength,
        Action<LightDownloadProgress>? progressChanged,
        CancellationToken ct)
    {
        EnsureFreeSpace(tempPath, totalLength);

        for (var attempt = 0;; attempt++)
        {
            var downloaded = new AtomicLong();
            var sw = Stopwatch.StartNew();
            using var progressCts = new CancellationTokenSource();
            var progressTask = ReportProgressOnlyLoop(progressCts.Token, downloaded.Read, totalLength, progressChanged);
            try
            {
                using var response = await SendRequestAsync(target, ct).ConfigureAwait(false);

                await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using (var dest = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.Read,
                                 bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    var buffer = ArrayPool<byte>.Shared.Rent(_config.BufferSize);
                    try
                    {
                        while (true)
                        {
                            var read = await ReadWithStallTimeoutAsync(source, buffer, 0, _config.NoDataTimeout, ct)
                                .ConfigureAwait(false);
                            if (read == 0)
                                break;

                            await WriteAsync(dest, buffer, read, tempPath, ct).ConfigureAwait(false);
                            downloaded.Add(read);
                            await ApplySpeedLimitAsync(downloaded.Read(), sw, ct).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }

                var received = downloaded.Read();
                if (totalLength >= 0 && received != totalLength)
                    throw new SegmentRetryException(0,
                        $"the server announced {totalLength} bytes but sent {received}.");

                return received;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsRetryable(ex) && attempt < _config.MaxRetry)
            {
                var delay = GetRetryDelay(attempt, (ex as SegmentRetryException)?.RetryAfter);
                NotifyRetry(0, totalLength - 1, attempt + 1, delay, ex);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            finally
            {
                await StopProgressReportingAsync(progressCts, progressTask).ConfigureAwait(false);
                sw.Stop();
            }
        }
    }

    private async Task<ProbeResult> ProbeFileInfoAsync(Uri url, IReadOnlyDictionary<string, string>? headers,
        CancellationToken ct)
    {
        // The probe is a network request like any other: a transient failure here must not sink a
        // download that the retry budget could easily have saved.
        for (var attempt = 0;; attempt++)
        {
            try
            {
                return await ProbeOnceAsync(url, headers, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (!IsRetryable(ex) || attempt >= _config.MaxRetry)
                    throw Rethrow(ex);

                var delay = GetRetryDelay(attempt, (ex as SegmentRetryException)?.RetryAfter);
                NotifyRetry(0, -1, attempt + 1, delay, ex);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<ProbeResult> ProbeOnceAsync(Uri url, IReadOnlyDictionary<string, string>? headers,
        CancellationToken ct)
    {
        var response = await SendWithRedirectsAsync(url, headers, new RangeHeaderValue(0, 0), ifRange: null, ct)
            .ConfigureAwait(false);
        try
        {
            // Some servers reject a range probe outright; a plain GET still works for them.
            if (response.StatusCode is HttpStatusCode.RequestedRangeNotSatisfiable
                or HttpStatusCode.MethodNotAllowed
                or HttpStatusCode.NotImplemented)
            {
                response.Dispose();
                response = await SendWithRedirectsAsync(url, headers, range: null, ifRange: null, ct)
                    .ConfigureAwait(false);
            }

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = response.StatusCode;
                var retryAfter = GetRetryAfter(response);
                if (IsRetryableStatus(statusCode))
                    throw new SegmentRetryException(0,
                        $"the server answered {(int)statusCode} {statusCode} to the initial request.", retryAfter,
                        throttled: IsThrottleStatus(statusCode));

                throw new FatalDownloadException(
                    $"The server answered {(int)statusCode} {statusCode} to the initial request.");
            }

            var declinesRanges = response.Headers.AcceptRanges
                .Any(value => string.Equals(value, "none", StringComparison.OrdinalIgnoreCase));

            long size;
            bool supportsRange;
            if (response is { StatusCode: HttpStatusCode.PartialContent, Content.Headers.ContentRange.Length: { } length })
            {
                size = length;
                supportsRange = !declinesRanges;
            }
            else
            {
                size = response.Content.Headers.ContentLength ?? UnknownSize;
                supportsRange = false;
            }

            if (size < 0)
                supportsRange = false;

            // A gated origin answers 200 with its challenge page. Accepting it here would save a few
            // kilobytes of HTML under the requested file's name and report success.
            if (_config.DetectChallengePages && IsChallengePage(response))
                throw new SegmentRetryException(0,
                    "the server answered with a challenge or rate-limit page instead of the file; " +
                    "any session cookie may need refreshing.",
                    GetRetryAfter(response), throttled: true);

            var downloadUri = response.RequestMessage?.RequestUri ?? url;
            var eTag = response.Headers.ETag;
            var lastModified = response.Content.Headers.LastModified?
                .ToUniversalTime()
                .ToString("R", CultureInfo.InvariantCulture);

            return new ProbeResult(
                GetFileName(downloadUri, response),
                size,
                response.Content.Headers.ContentType?.ToString(),
                supportsRange,
                downloadUri,
                eTag?.ToString(),
                // Only a strong ETag is trustworthy here. A weak one only proves semantic
                // equivalence, and Last-Modified is restamped per response by some origins
                // (signed CDN URLs), which would make every range request fail validation.
                eTag is { IsWeak: false } ? eTag.ToString() : null,
                lastModified);
        }
        finally
        {
            response.Dispose();
        }
    }

    private async Task DownloadChunkAsync(
        DownloadTarget target,
        string path,
        long start,
        long end,
        Action<long> onBytesReceived,
        Func<long> getSessionDownloaded,
        Func<int> getActiveSegmentCount,
        Func<TimeSpan> getStallTimeout,
        long totalLength,
        Stopwatch globalStopwatch,
        string? ifRange,
        string? expectedContentType,
        Action<long, long> onRangeCompleted,
        CancellationToken ct)
    {
        var currentOffset = start;
        var lastCommittedOffset = start;
        var segmentBytes = 0L;
        var segmentStopwatch = Stopwatch.StartNew();
        var buffer = ArrayPool<byte>.Shared.Rent(_config.BufferSize);

        try
        {
            using var response = await SendRangeRequestAsync(target, start, end, totalLength, ifRange, expectedContentType, ct)
                .ConfigureAwait(false);

            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dest = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite,
                bufferSize: 1, FileOptions.Asynchronous);

            dest.Seek(start, SeekOrigin.Begin);

            while (currentOffset <= end)
            {
                var read = await ReadWithStallTimeoutAsync(source, buffer, currentOffset, getStallTimeout(), ct)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;

                // Never write past the requested range, whatever the server decides to send.
                var writable = (int)Math.Min(read, end - currentOffset + 1);
                await WriteAsync(dest, buffer, writable, path, ct).ConfigureAwait(false);
                currentOffset += writable;
                segmentBytes += writable;
                onBytesReceived(writable);

                if (currentOffset - lastCommittedOffset >= MetadataFlushBytes)
                {
                    await FlushAsync(dest, ct).ConfigureAwait(false);
                    onRangeCompleted(lastCommittedOffset, currentOffset - 1);
                    lastCommittedOffset = currentOffset;
                }

                await ApplySpeedLimitAsync(getSessionDownloaded(), globalStopwatch, ct).ConfigureAwait(false);

                var remainingBytes = end - currentOffset + 1;
                if (remainingBytes >= _config.MinRemainingBytesForRequeue &&
                    IsSlowSegment(segmentBytes, segmentStopwatch, getSessionDownloaded, getActiveSegmentCount,
                        globalStopwatch))
                    throw new SegmentRetryException(currentOffset,
                        "the connection is much slower than the global average and will be requeued.");
            }

            if (currentOffset > lastCommittedOffset)
            {
                await FlushAsync(dest, ct).ConfigureAwait(false);
                onRangeCompleted(lastCommittedOffset, currentOffset - 1);
                lastCommittedOffset = currentOffset;
            }

            // A clean EOF before the end of the range still means the range is incomplete.
            if (currentOffset <= end)
                throw new SegmentRetryException(currentOffset,
                    $"the server closed the connection {end - currentOffset + 1} byte(s) before the end of the range.");
        }
        catch (SegmentRetryException)
        {
            throw;
        }
        catch (FatalDownloadException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Bytes already handed to the OS survive a process crash, so recording them is safe and
            // saves re-downloading them after a cancel/resume cycle.
            if (currentOffset > lastCommittedOffset)
                onRangeCompleted(lastCommittedOffset, currentOffset - 1);

            throw;
        }
        catch (Exception ex)
        {
            throw new SegmentRetryException(currentOffset, "the segment download failed.", ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async ValueTask<int> ReadWithStallTimeoutAsync(Stream source, byte[] buffer, long offset,
        TimeSpan stallTimeout, CancellationToken ct)
    {
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(stallTimeout);

        try
        {
            return await source.ReadAsync(buffer.AsMemory(0, _config.BufferSize), readCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SegmentRetryException(offset,
                $"no data was received for {stallTimeout.TotalSeconds:0.#}s; the connection will be requeued.");
        }
    }

    /// <summary>
    /// A stalled connection is only worth waiting on while every worker is busy. Once some are
    /// idle, a spare one picks the range up the moment it is requeued, so holding the full timeout
    /// is dead air on an otherwise finished download - which is what the last few percent look like.
    /// </summary>
    private TimeSpan GetStallTimeout(int activeSegments, int concurrency)
    {
        if (activeSegments >= concurrency)
            return _config.NoDataTimeout;

        var shortened = _config.NoDataTimeout / 4;
        if (shortened < MinIdleStallTimeout)
            shortened = MinIdleStallTimeout;

        return shortened < _config.NoDataTimeout ? shortened : _config.NoDataTimeout;
    }

    private static async ValueTask WriteAsync(FileStream dest, byte[] buffer, int count, string path,
        CancellationToken ct)
    {
        try
        {
            await dest.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A local storage failure (out of space, permissions, unplugged volume) will not fix
            // itself by retrying the request.
            throw new FatalDownloadException($"Failed to write to '{path}': {ex.Message}", ex);
        }
    }

    private async ValueTask FlushAsync(FileStream dest, CancellationToken ct)
    {
        await dest.FlushAsync(ct).ConfigureAwait(false);
        if (_config.DurableFlush)
            dest.Flush(flushToDisk: true);
    }

    private bool IsSlowSegment(
        long segmentBytes,
        Stopwatch segmentStopwatch,
        Func<long> getSessionDownloaded,
        Func<int> getActiveSegmentCount,
        Stopwatch globalStopwatch)
    {
        if (segmentStopwatch.Elapsed < _config.SlowSegmentMinDuration)
            return false;

        // Near the end only a few segments remain, and the session average still reflects the
        // full width of the download. Comparing one connection against that aggregate marks the
        // last segment slow forever, so it is requeued again and again and never finishes.
        if (getActiveSegmentCount() < 2)
            return false;

        var globalSeconds = globalStopwatch.Elapsed.TotalSeconds;
        var segmentSeconds = segmentStopwatch.Elapsed.TotalSeconds;
        if (globalSeconds <= 0 || segmentSeconds <= 0)
            return false;

        var globalSpeed = getSessionDownloaded() / globalSeconds;
        var averageConnectionSpeed = globalSpeed / Math.Max(getActiveSegmentCount(), 1);
        var segmentSpeed = segmentBytes / segmentSeconds;
        return averageConnectionSpeed > 0 && segmentSpeed < averageConnectionSpeed * _config.SlowSpeedRatio;
    }

    private static async Task StopProgressReportingAsync(CancellationTokenSource cancellation, Task progressTask)
    {
        await cancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await progressTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    private async Task ReportProgressOnlyLoop(CancellationToken ct, Func<long> getDownloaded, long total,
        Action<LightDownloadProgress>? progressChanged)
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

            progressChanged?.Invoke(new LightDownloadProgress
            {
                DownloadedBytes = nowBytes,
                TotalBytes = Math.Max(total, 0),
                Speed = speed,
            });
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
        Action<long> setSegmentSize,
        ThrottleController? throttle,
        int maxConcurrency,
        Action<TimeSpan> onStalled)
    {
        var lastBytes = getDownloaded();
        var lastTime = Stopwatch.GetTimestamp();
        var lastAdapt = DateTimeOffset.UtcNow;
        var previousSpeed = 0d;
        var lastProgressBytes = lastBytes;
        var lastProgressTime = Stopwatch.GetTimestamp();

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_config.ProgressIntervalMs, ct).ConfigureAwait(false);
            var nowBytes = getDownloaded();
            var nowTime = Stopwatch.GetTimestamp();
            var seconds = (nowTime - lastTime) / (double)Stopwatch.Frequency;
            var speed = seconds > 0 ? (nowBytes - lastBytes) / seconds : 0;

            progressChanged?.Invoke(new LightDownloadProgress
            {
                DownloadedBytes = nowBytes,
                TotalBytes = Math.Max(total, 0),
                Speed = speed,
            });

            if (DateTimeOffset.UtcNow - lastAdapt >= _config.AdaptInterval)
            {
                throttle?.Recover(maxConcurrency);
                AdaptDownloadParameters(speed, previousSpeed, getConcurrency, setConcurrency, getSegmentSize,
                    setSegmentSize, Math.Min(throttle?.Ceiling ?? maxConcurrency, maxConcurrency));
                previousSpeed = speed;
                lastAdapt = DateTimeOffset.UtcNow;
            }

            lastBytes = nowBytes;
            lastTime = nowTime;

            if (nowBytes > lastProgressBytes)
            {
                lastProgressBytes = nowBytes;
                lastProgressTime = nowTime;
            }
            else if (_config.StallTimeout > TimeSpan.Zero)
            {
                var stalledFor = Stopwatch.GetElapsedTime(lastProgressTime, nowTime);
                if (stalledFor >= _config.StallTimeout)
                {
                    onStalled(stalledFor);
                    return;
                }
            }
        }
    }

    private void AdaptDownloadParameters(
        double speed,
        double previousSpeed,
        Func<int> getConcurrency,
        Action<int> setConcurrency,
        Func<long> getSegmentSize,
        Action<long> setSegmentSize,
        int concurrencyCeiling)
    {
        if (previousSpeed <= 0 || speed <= 0)
            return;

        var concurrency = getConcurrency();
        var segmentSize = getSegmentSize();

        if (speed > previousSpeed * 1.08)
        {
            if (_config.EnableDynamicConcurrency && concurrency < concurrencyCeiling)
                setConcurrency(concurrency + 1);

            if (_config.EnableDynamicSegmentSize && segmentSize < _config.MaxSegmentSize)
                setSegmentSize(Math.Min(segmentSize * 2, _config.MaxSegmentSize));
        }
        else if (speed < previousSpeed * 0.85)
        {
            // Adding connections to a link that just got slower makes it worse - back off instead.
            if (_config.EnableDynamicConcurrency && concurrency > _config.MinChunkCount)
                setConcurrency(concurrency - 1);

            if (_config.EnableDynamicSegmentSize && segmentSize > _config.MinSegmentSize)
                setSegmentSize(Math.Max(segmentSize / 2, _config.MinSegmentSize));
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
        var workers = Math.Max(concurrency, 1);

        // Two segments per worker: enough granularity to rebalance away from a slow connection,
        // few enough that the per-request round trip stays amortised. That round trip is what
        // hurts on origins that throttle each connection - a 16 MB segment there finishes in a
        // couple of seconds and the next request costs a visible slice of the transfer.
        // Measured on such an origin, 16 conns: 16 MB -> 58 MB/s, 48 MB -> 88 MB/s.
        // Small files still divide down so every worker gets something to do.
        var sizeForConcurrency = (long)Math.Ceiling(totalLength / (double)(workers * 2));

        return Math.Clamp(
            Math.Min(_config.SegmentSize, sizeForConcurrency),
            _config.BufferSize,
            _config.MaxSegmentSize);
    }

    private TimeSpan GetRetryDelay(int retryCount, TimeSpan? retryAfter)
    {
        if (retryAfter is { } serverDelay)
            return serverDelay < TimeSpan.Zero ? TimeSpan.Zero :
                serverDelay > _config.MaxRetryDelay ? _config.MaxRetryDelay : serverDelay;

        var exponent = Math.Min(retryCount, 16);
        var delayMs = _config.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, exponent);
        var capped = Math.Min(delayMs, _config.MaxRetryDelay.TotalMilliseconds);
        // Jitter keeps a wave of failed segments from hammering the server in lockstep.
        var jittered = capped * (0.8 + Random.Shared.NextDouble() * 0.4);
        return TimeSpan.FromMilliseconds(Math.Max(jittered, 0));
    }

    private void NotifyRetry(long start, long end, int attempt, TimeSpan delay, Exception error)
    {
        if (_config.RetryHandler is not { } handler)
            return;

        SafeInvoke(() => handler(new LightDownloadRetry
        {
            Start = start,
            End = end,
            Attempt = attempt,
            Delay = delay,
            Error = error
        }));
    }

    private static bool IsRetryable(Exception exception) => exception switch
    {
        FatalDownloadException => false,
        SegmentRetryException => true,
        HttpRequestException => true,
        // HttpIOException (premature end of stream) and socket errors both land here. Local disk
        // failures are wrapped as FatalDownloadException before they can reach this point.
        IOException => true,
        _ => false
    };

    private static bool IsPartialDataInvalid(Exception exception) =>
        exception is FatalDownloadException { DiscardPartialData: true } or LightDownloadException;

    /// <summary>
    /// Presents a single exception type to callers. Internal signalling types must never escape,
    /// and a transfer failure should not surface a different type depending on whether the server
    /// reported a status or the socket simply died. Cancellation keeps its own semantics.
    /// </summary>
    private static Exception Rethrow(Exception exception)
    {
        return exception switch
        {
            OperationCanceledException or LightDownloadException => exception,
            FatalDownloadException fatal => new LightDownloadException(fatal.Message, fatal.InnerException ?? fatal),
            SegmentRetryException retry => new LightDownloadException(
                $"The download failed: {retry.Message}", retry.InnerException ?? retry),
            HttpRequestException or IOException => new LightDownloadException(
                $"The download failed: {exception.Message}", exception),
            _ => exception
        };
    }

    private DownloadMetadata LoadOrCreateMetadata(string url, long totalLength, ProbeResult probe, string tempPath,
        string metadataPath)
    {
        var fresh = new DownloadMetadata(url, totalLength, [])
        {
            ETag = probe.ETag,
            LastModified = probe.LastModified
        };

        if (!_config.EnableResume || !File.Exists(tempPath) || !File.Exists(metadataPath))
        {
            DeleteIfExists(metadataPath);
            return fresh;
        }

        try
        {
            var json = File.ReadAllText(metadataPath);
            var metadata = JsonSerializer.Deserialize(json, LightDlJsonContext.Default.DownloadMetadata);
            if (metadata is not null &&
                metadata.Url == url &&
                metadata.TotalLength == totalLength &&
                ValidatorsMatch(metadata, probe))
                return metadata;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Broken metadata means a fresh download is safer.
        }

        DeleteIfExists(tempPath);
        DeleteIfExists(metadataPath);
        return fresh;
    }

    /// <summary>
    /// URL and length alone do not prove the bytes on the server are still the same bytes that were
    /// partially downloaded, so an ETag/Last-Modified change forces a restart.
    /// </summary>
    private static bool ValidatorsMatch(DownloadMetadata metadata, ProbeResult probe)
    {
        if (metadata.ETag is not null || probe.ETag is not null)
            return string.Equals(metadata.ETag, probe.ETag, StringComparison.Ordinal);

        // Deliberately not comparing Last-Modified: origins that restamp it per response would
        // discard a perfectly good partial download on every resume.
        return true;
    }

    private void AddCompletedRange(DownloadMetadata metadata, string metadataPath, long start, long end)
    {
        if (end < start)
            return;

        string json;
        long version;
        lock (_metadataLock)
        {
            InsertRange(metadata.CompletedRanges, start, end);
            if (!_config.EnableResume)
                return;

            var now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(_lastMetadataSaveTimestamp, now) < _config.MetadataFlushInterval)
                return;

            _lastMetadataSaveTimestamp = now;
            version = ++_metadataVersion;
            json = JsonSerializer.Serialize(metadata, LightDlJsonContext.Default.DownloadMetadata);
        }

        WriteMetadata(json, version, metadataPath);
    }

    private void ForceSaveMetadata(DownloadMetadata metadata, string metadataPath)
    {
        if (!_config.EnableResume)
            return;

        string json;
        long version;
        lock (_metadataLock)
        {
            _lastMetadataSaveTimestamp = Stopwatch.GetTimestamp();
            version = ++_metadataVersion;
            json = JsonSerializer.Serialize(metadata, LightDlJsonContext.Default.DownloadMetadata);
        }

        WriteMetadata(json, version, metadataPath);
    }

    private void WriteMetadata(string json, long version, string metadataPath)
    {
        lock (_metadataFileLock)
        {
            if (version <= _metadataWrittenVersion)
                return;

            _metadataWrittenVersion = version;
            SaveMetadata(json, metadataPath, _config.HideMetadataFile);
        }
    }

    private static void SaveMetadata(string json, string metadataPath, bool hide)
    {
        var tempPath = metadataPath + ".tmp";
        try
        {
            File.WriteAllText(tempPath, json);
            if (hide && OperatingSystem.IsWindows())
                File.SetAttributes(tempPath, FileAttributes.Hidden);

            if (File.Exists(metadataPath))
                File.Replace(tempPath, metadataPath, null);
            else
                File.Move(tempPath, metadataPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Resume metadata is an optimisation: losing it costs a re-download, not correctness.
            DeleteIfExists(tempPath);
        }
    }

    private void EnsureAllRangesComplete(DownloadMetadata metadata, long totalLength)
    {
        List<DownloadRange> missing;
        lock (_metadataLock)
        {
            metadata.CompletedRanges = MergeRanges(metadata.CompletedRanges);
            missing = BuildMissingRanges(totalLength, metadata.CompletedRanges);
        }

        if (missing.Count == 0)
            return;

        var missingBytes = missing.Sum(range => range.End - range.Start + 1);
        throw new LightDownloadException(
            $"The download ended with {missingBytes} byte(s) missing across {missing.Count} range(s).");
    }

    private void EnsureFreeSpace(string path, long requiredBytes)
    {
        if (!_config.CheckFreeSpace || requiredBytes <= 0)
            return;

        long available;
        string root;
        try
        {
            root = Path.GetPathRoot(Path.GetFullPath(path)) ?? string.Empty;
            if (root.Length == 0)
                return;

            available = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
                                       or PlatformNotSupportedException)
        {
            // Free space cannot be determined for every mount; carry on and let the write fail.
            return;
        }

        if (available < requiredBytes)
            throw new LightDownloadException(
                $"Not enough free space on '{root}': {requiredBytes} byte(s) required, {available} available.");
    }

    private async Task VerifyChecksumAsync(string path, CancellationToken ct)
    {
        if (_config.ChecksumAlgorithm == LightDownloadChecksumAlgorithm.None ||
            string.IsNullOrWhiteSpace(_config.ExpectedChecksum))
            return;

        byte[] hash;
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                         bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            hash = _config.ChecksumAlgorithm switch
            {
                LightDownloadChecksumAlgorithm.Md5 => await MD5.HashDataAsync(stream, ct).ConfigureAwait(false),
                LightDownloadChecksumAlgorithm.Sha1 => await SHA1.HashDataAsync(stream, ct).ConfigureAwait(false),
                LightDownloadChecksumAlgorithm.Sha256 => await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false),
                LightDownloadChecksumAlgorithm.Sha512 => await SHA512.HashDataAsync(stream, ct).ConfigureAwait(false),
                _ => throw new ArgumentOutOfRangeException(nameof(LightDownloadConfig.ChecksumAlgorithm),
                    _config.ChecksumAlgorithm, "Unknown checksum algorithm.")
            };
        }

        var actual = Convert.ToHexString(hash);
        var expected = _config.ExpectedChecksum.Trim().Replace("-", string.Empty);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new LightDownloadException(
                $"{_config.ChecksumAlgorithm} checksum mismatch: expected {expected.ToLowerInvariant()}, got {actual.ToLowerInvariant()}.");
    }

    private static void Preallocate(string path, long totalLength)
    {
        using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite, 4096,
            FileOptions.None);
        fs.SetLength(totalLength);
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup is best-effort. Preserve the original download exception.
        }
    }

    private static List<DownloadRange> BuildMissingRanges(long totalLength, List<CompletedRange> completedRanges)
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

    private static List<CompletedRange> MergeRanges(List<CompletedRange> ranges)
    {
        if (ranges.Count == 0)
            return ranges;

        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<CompletedRange> { new(ranges[0].Start, ranges[0].End) };
        for (var i = 1; i < ranges.Count; i++)
        {
            var last = merged[^1];
            var current = ranges[i];
            if (current.Start <= last.End + 1)
                last.End = Math.Max(last.End, current.End);
            else
                merged.Add(new CompletedRange(current.Start, current.End));
        }

        return merged;
    }

    /// <summary>
    /// Inserts a range into an already sorted, non-overlapping list and merges it with its
    /// neighbours. Avoids re-sorting and re-allocating the whole list on every commit.
    /// </summary>
    private static void InsertRange(List<CompletedRange> ranges, long start, long end)
    {
        var index = 0;
        while (index < ranges.Count && ranges[index].Start < start)
            index++;

        ranges.Insert(index, new CompletedRange(start, end));

        for (var i = index > 0 ? index - 1 : 0; i < ranges.Count - 1;)
        {
            if (ranges[i + 1].Start <= ranges[i].End + 1)
            {
                ranges[i].End = Math.Max(ranges[i].End, ranges[i + 1].End);
                ranges.RemoveAt(i + 1);
                continue;
            }

            if (i >= index)
                break;

            i++;
        }
    }

    private async Task<HttpResponseMessage> SendRangeRequestAsync(
        DownloadTarget target,
        long start,
        long end,
        long totalLength,
        string? ifRange,
        string? expectedContentType,
        CancellationToken ct)
    {
        while (true)
        {
            var requestTarget = target.GetRequestTarget();
            HttpResponseMessage response;
            try
            {
                response = await SendWithRedirectsAsync(requestTarget.Uri, requestTarget.Headers,
                    new RangeHeaderValue(start, end), ifRange, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (requestTarget.IsDirect)
            {
                target.Fallback();
                continue;
            }
            catch (OperationCanceledException) when (requestTarget.IsDirect && !ct.IsCancellationRequested)
            {
                target.Fallback();
                continue;
            }
            catch (HttpRequestException ex)
            {
                throw new SegmentRetryException(start, "the range request could not be sent.", ex);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new SegmentRetryException(start, "the range request timed out.", ex);
            }

            if (response.StatusCode == HttpStatusCode.PartialContent &&
                IsValidContentRange(response.Content.Headers.ContentRange, start, end, totalLength))
                return response;

            // A signed download URL captured at probe time can expire mid-download. Re-resolving
            // through the original URL mints a new one, so that must be tried before concluding
            // anything about the response - including an If-Range miss.
            if (requestTarget.IsDirect)
            {
                target.Fallback();
                response.Dispose();
                continue;
            }

            if (response.StatusCode == HttpStatusCode.OK)
            {
                var interstitial = IsInterstitialResponse(response, end - start + 1, expectedContentType);
                var gateRetryAfter = GetRetryAfter(response);
                response.Dispose();

                if (interstitial)
                    throw new SegmentRetryException(start,
                        "the server answered 200 with a challenge or error page instead of the requested range.",
                        gateRetryAfter, throttled: true);

                // 200 in answer to If-Range means the validator no longer matches: the file changed.
                if (ifRange is not null)
                    throw new FatalDownloadException(
                        "The remote file changed while it was being downloaded; the partial data is no longer valid.",
                        discardPartialData: true);

                throw new FatalDownloadException("The server answered 200 OK to a range request.");
            }

            var statusCode = response.StatusCode;
            var retryAfter = GetRetryAfter(response);
            response.Dispose();

            if (IsRetryableStatus(statusCode))
                throw new SegmentRetryException(start,
                    $"the server answered {(int)statusCode} {statusCode} to a range request.", retryAfter,
                    throttled: IsThrottleStatus(statusCode));

            throw new FatalDownloadException(
                $"The server answered {(int)statusCode} {statusCode} to a range request.",
                discardPartialData: statusCode == HttpStatusCode.RequestedRangeNotSatisfiable);
        }
    }

    private async Task<HttpResponseMessage> SendRequestAsync(DownloadTarget target, CancellationToken ct)
    {
        while (true)
        {
            var requestTarget = target.GetRequestTarget();
            HttpResponseMessage response;
            try
            {
                response = await SendWithRedirectsAsync(requestTarget.Uri, requestTarget.Headers, range: null,
                    ifRange: null, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (requestTarget.IsDirect)
            {
                target.Fallback();
                continue;
            }
            catch (OperationCanceledException) when (requestTarget.IsDirect && !ct.IsCancellationRequested)
            {
                target.Fallback();
                continue;
            }

            if (response.IsSuccessStatusCode)
                return response;

            var statusCode = response.StatusCode;
            var retryAfter = GetRetryAfter(response);
            response.Dispose();

            if (requestTarget.IsDirect)
            {
                target.Fallback();
                continue;
            }

            if (IsRetryableStatus(statusCode))
                throw new SegmentRetryException(0, $"the server answered {(int)statusCode} {statusCode}.", retryAfter,
                    throttled: IsThrottleStatus(statusCode));

            throw new FatalDownloadException($"The server answered {(int)statusCode} {statusCode}.");
        }
    }

    /// <summary>
    /// A 200 answering a range request is supposed to carry the whole entity. Rate-limit and
    /// anti-bot gates answer 200 with a small HTML page instead, and reading that as "the file
    /// changed" throws away every byte downloaded so far. Neither signal is conclusive alone, so a
    /// body too small to be the requested range, or one that turned into HTML when the file was
    /// not HTML, is treated as a gate and retried rather than discarded.
    /// </summary>
    private bool IsInterstitialResponse(
        HttpResponseMessage response,
        long requestedLength,
        string? expectedContentType)
    {
        // A body shorter than the range that was asked for cannot be that range, whatever it claims.
        if (response.Content.Headers.ContentLength is { } length && length < requestedLength)
            return true;

        return _config.DetectChallengePages &&
               IsHtml(response.Content.Headers.ContentType?.MediaType) &&
               !IsHtml(expectedContentType);
    }

    private static bool IsChallengePage(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.OK &&
        IsHtml(response.Content.Headers.ContentType?.MediaType) &&
        response.Content.Headers.ContentLength is { } length &&
        length <= MaxChallengePageBytes;

    private static bool IsHtml(string? contentType) =>
        contentType is not null && contentType.Contains("html", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Statuses that mean "you are asking too often" rather than "something broke". These drive the
    /// connection count down; the rest are retried without touching it.
    /// </summary>
    private static bool IsThrottleStatus(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.TooManyRequests or
        HttpStatusCode.ServiceUnavailable or
        (HttpStatusCode)509;

    private static bool IsRetryableStatus(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.RequestTimeout or
        HttpStatusCode.TooManyRequests or
        HttpStatusCode.InternalServerError or
        HttpStatusCode.BadGateway or
        HttpStatusCode.ServiceUnavailable or
        HttpStatusCode.GatewayTimeout or
        HttpStatusCode.InsufficientStorage or
        (HttpStatusCode)425 or
        (HttpStatusCode)509;

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
            return null;

        if (retryAfter.Delta is { } delta)
            return delta;

        if (retryAfter.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    private static bool IsValidContentRange(ContentRangeHeaderValue? contentRange, long start, long end,
        long totalLength)
    {
        return contentRange?.From == start && contentRange.To == end && contentRange.Length == totalLength;
    }

    private async Task<HttpResponseMessage> SendWithRedirectsAsync(
        Uri uri,
        IReadOnlyDictionary<string, string>? headers,
        RangeHeaderValue? range,
        string? ifRange,
        CancellationToken ct)
    {
        var currentUri = uri;
        var currentHeaders = headers;

        for (var redirectCount = 0;; redirectCount++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            request.Headers.Range = range;
            // Any content coding would break the mapping between response bytes and file offsets.
            request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
            if (range is not null && ifRange is not null)
                request.Headers.TryAddWithoutValidation("If-Range", ifRange);

            ApplyHeaders(request, currentHeaders);

            var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode) || response.Headers.Location is not { } location)
                return response;

            if (redirectCount >= MaxRedirects)
            {
                response.Dispose();
                throw new HttpRequestException($"The request exceeded the maximum of {MaxRedirects} redirects.");
            }

            var redirectUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
            if (!HaveSameAuthority(currentUri, redirectUri))
                currentHeaders = WithoutSensitiveHeaders(currentHeaders);

            currentUri = redirectUri;
            response.Dispose();
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

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

    private static bool HaveSameAuthority(Uri first, Uri second)
    {
        return string.Equals(first.Scheme, second.Scheme, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(first.Host, second.Host, StringComparison.OrdinalIgnoreCase) &&
               first.Port == second.Port;
    }

    private static IReadOnlyDictionary<string, string>? WithoutSensitiveHeaders(
        IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || !headers.Keys.Any(IsSensitiveHeader))
            return headers;

        return headers
            .Where(header => !IsSensitiveHeader(header.Key))
            .ToDictionary(header => header.Key, header => header.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsSensitiveHeader(string name) =>
        string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Cookie", StringComparison.OrdinalIgnoreCase);

    private static string GetFileName(Uri url, HttpResponseMessage response)
    {
        var contentDisposition = response.Content.Headers.ContentDisposition;
        if (!string.IsNullOrWhiteSpace(contentDisposition?.FileNameStar))
            return SanitizeFileName(contentDisposition.FileNameStar.Trim('"'));

        if (!string.IsNullOrWhiteSpace(contentDisposition?.FileName))
        {
            var fileName = contentDisposition.FileName.Trim('"');
            return SanitizeFileName(TryRepairUtf8Mojibake(fileName));
        }

        // Uri.Segments keeps the percent-encoding, so this decodes exactly once.
        var segment = url.Segments.Length > 0 ? url.Segments[^1].TrimEnd('/') : string.Empty;
        if (segment.Length > 0)
        {
            var decoded = Uri.UnescapeDataString(segment);
            if (!string.IsNullOrWhiteSpace(decoded))
                return SanitizeFileName(decoded);
        }

        return DefaultFileName;
    }

    private static string TryRepairUtf8Mojibake(string fileName)
    {
        if (fileName.All(character => character <= 0x7f) || fileName.Any(character => character > byte.MaxValue))
            return fileName;

        try
        {
            var repaired = StrictUtf8.GetString(Encoding.Latin1.GetBytes(fileName));
            return repaired.Any(character => character > 0x7f) &&
                   (HasUtf8MojibakeMarkers(fileName) || ContainsCjkCharacter(repaired))
                ? repaired
                : fileName;
        }
        catch (DecoderFallbackException)
        {
            return fileName;
        }
    }

    private static bool HasUtf8MojibakeMarkers(string value)
    {
        return value.IndexOfAny(['Ã', 'Â', 'â', 'ð', 'Ð', 'Ñ', 'Î', 'Ï']) >= 0 ||
               value.Any(character => character is >= '\u0080' and <= '\u009f');
    }

    private static bool ContainsCjkCharacter(string value)
    {
        return value.Any(character => character is >= '\u3040' and <= '\u30ff' or
            >= '\u3400' and <= '\u4dbf' or
            >= '\u4e00' and <= '\u9fff' or
            >= '\uac00' and <= '\ud7af');
    }

    private bool TryHandleExistingFile(LightDownloadFileInfo info, ref string destinationPath,
        out LightDownloadResult result)
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
                result = CreateDownloadResult(info, destinationPath, skipped: true,
                    size: new FileInfo(destinationPath).Length);
                return true;

            case LightDownloadFileConflictPolicy.Rename:
                destinationPath = GetUniqueFilePath(destinationPath);
                return false;

            default:
                throw new ArgumentOutOfRangeException(nameof(LightDownloadConfig.FileConflictPolicy),
                    _config.FileConflictPolicy, "Unknown file conflict policy.");
        }
    }

    private static string ResolveDestinationPath(string path, string fileName,
        LightDownloadDestinationKind destinationKind)
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
                throw new ArgumentOutOfRangeException(nameof(destinationKind), destinationKind,
                    "Unknown destination kind.");
        }
    }

    /// <summary>
    /// Resolves where the resume metadata lives. On Unix "hidden" means a leading dot on the file
    /// name, so the path itself changes; on Windows the name is unchanged and the Hidden attribute
    /// is applied when the file is written.
    /// </summary>
    private string ResolveMetadataPath(string destinationPath)
    {
        var visiblePath = destinationPath + _config.MetadataFileExtension;
        if (!_config.HideMetadataFile || OperatingSystem.IsWindows())
            return visiblePath;

        var fileName = Path.GetFileName(visiblePath);
        if (fileName.StartsWith('.'))
            return visiblePath;

        var directory = Path.GetDirectoryName(visiblePath);
        var hiddenPath = string.IsNullOrEmpty(directory)
            ? "." + fileName
            : Path.Combine(directory, "." + fileName);

        // An older version wrote this file under the visible name; adopt it instead of restarting
        // the download and leaving the old file orphaned.
        if (File.Exists(visiblePath) && !File.Exists(hiddenPath))
        {
            try
            {
                File.Move(visiblePath, hiddenPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return visiblePath;
            }
        }

        return hiddenPath;
    }

    private static string GetUniqueFilePath(string path)
    {
        var directory = Path.GetDirectoryName(path);
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var i = 1;; i++)
        {
            var candidateName = string.IsNullOrEmpty(extension) ? $"{name} ({i})" : $"{name} ({i}){extension}";
            var candidate = string.IsNullOrWhiteSpace(directory)
                ? candidateName
                : Path.Combine(directory, candidateName);
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    private static LightDownloadResult CreateDownloadResult(LightDownloadFileInfo info, string destinationPath,
        bool skipped = false, long? size = null)
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

    /// <summary>
    /// Reduces a server-supplied name to something that can only ever create a single file inside
    /// the destination directory.
    /// </summary>
    internal static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(fileName.Length);
        foreach (var character in fileName)
        {
            var invalid = char.IsControl(character) ||
                          character is '/' or '\\' ||
                          Array.IndexOf(invalidChars, character) >= 0;
            builder.Append(invalid ? '_' : character);
        }

        // Trailing dots and spaces are silently stripped by Windows, which turns "a. " into "a".
        var result = builder.ToString().Trim().TrimEnd('.', ' ');
        if (result.Length == 0 || result is "." or "..")
            return DefaultFileName;

        if (OperatingSystem.IsWindows() && IsWindowsReservedName(result))
            result = "_" + result;

        return TruncateFileName(result);
    }

    private static bool IsWindowsReservedName(string fileName)
    {
        var dot = fileName.IndexOf('.');
        var stem = dot < 0 ? fileName : fileName[..dot];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase))
            return true;

        return stem.Length == 4 &&
               (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
               char.IsAsciiDigit(stem[3]);
    }

    private static string TruncateFileName(string fileName)
    {
        if (Encoding.UTF8.GetByteCount(fileName) <= MaxFileNameBytes)
            return fileName;

        var extension = Path.GetExtension(fileName);
        if (Encoding.UTF8.GetByteCount(extension) > 32)
            extension = string.Empty;

        var stem = fileName[..^extension.Length];
        var budget = MaxFileNameBytes - Encoding.UTF8.GetByteCount(extension);
        while (stem.Length > 0 && Encoding.UTF8.GetByteCount(stem) > budget)
            stem = stem[..^1];

        if (stem.Length > 0 && char.IsHighSurrogate(stem[^1]))
            stem = stem[..^1];

        return stem.Length == 0 ? DefaultFileName : stem + extension;
    }

    private static Action<LightDownloadProgress>? BuildProgressReporter(
        IProgress<LightDownloadProgress>? progress,
        LightDownloadRequest request)
    {
        var handler = request.ProgressHandler;
        if (progress is null && handler is null)
            return null;

        return value =>
        {
            SafeInvoke(() => progress?.Report(value));
            SafeInvoke(() => handler?.Invoke(value));
        };
    }

    /// <summary>Caller callbacks must never be able to fail the download they are observing.</summary>
    private static void SafeInvoke(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // Ignored on purpose.
        }
    }

    private static void ValidateHandler(HttpMessageHandler handler)
    {
        var current = handler;
        while (current is DelegatingHandler { InnerHandler: { } inner })
            current = inner;

        switch (current)
        {
            case SocketsHttpHandler sockets:
                Check(sockets.AllowAutoRedirect, sockets.AutomaticDecompression);
                break;
            case HttpClientHandler client:
                Check(client.AllowAutoRedirect, client.AutomaticDecompression);
                break;
        }

        static void Check(bool allowAutoRedirect, DecompressionMethods decompression)
        {
            if (allowAutoRedirect)
                throw new ArgumentException(
                    "HttpMessageHandlerFactory must return a handler with AllowAutoRedirect disabled; LightDl follows redirects itself so that credentials can be stripped across origins.",
                    nameof(LightDownloadConfig.HttpMessageHandlerFactory));

            if (decompression != DecompressionMethods.None)
                throw new ArgumentException(
                    "HttpMessageHandlerFactory must return a handler with AutomaticDecompression disabled; decompressed bytes cannot be mapped back to byte ranges.",
                    nameof(LightDownloadConfig.HttpMessageHandlerFactory));
        }
    }

    private static void NormalizeConfig(LightDownloadConfig config)
    {
        config.BufferSize = Math.Max(config.BufferSize, 8 * 1024);

        // Ordering matters here: clamping against an un-normalised bound either throws or silently
        // overrides an explicit ChunkCount. MinChunkCount only bounds dynamic concurrency, so it
        // must never raise the worker count the caller asked for.
        config.ChunkCount = Math.Max(config.ChunkCount, 1);
        config.MinChunkCount = Math.Clamp(config.MinChunkCount, 1, config.ChunkCount);
        config.MaxChunkCount = Math.Max(config.MaxChunkCount, config.ChunkCount);

        config.MinSegmentSize = Math.Max(config.MinSegmentSize, config.BufferSize);
        config.MaxSegmentSize = Math.Max(config.MaxSegmentSize, config.MinSegmentSize);
        config.SegmentSize = Math.Clamp(config.SegmentSize, config.MinSegmentSize, config.MaxSegmentSize);

        config.ProgressIntervalMs = Math.Max(config.ProgressIntervalMs, 100);
        config.MaxRetry = Math.Max(config.MaxRetry, 0);

        if (config.Timeout <= TimeSpan.Zero)
            config.Timeout = Timeout.InfiniteTimeSpan;
        if (config.ConnectTimeout <= TimeSpan.Zero)
            config.ConnectTimeout = Timeout.InfiniteTimeSpan;
        if (config.NoDataTimeout <= TimeSpan.Zero)
            config.NoDataTimeout = TimeSpan.FromSeconds(15);
        if (config.RetryBaseDelay < TimeSpan.Zero)
            config.RetryBaseDelay = TimeSpan.Zero;
        if (config.MaxRetryDelay < config.RetryBaseDelay)
            config.MaxRetryDelay = config.RetryBaseDelay;
        if (config.MetadataFlushInterval < TimeSpan.Zero)
            config.MetadataFlushInterval = TimeSpan.Zero;

        if (config.ConnectionRampUpDelay < TimeSpan.Zero)
            config.ConnectionRampUpDelay = TimeSpan.Zero;
        if (config.ThrottleBackoffDelay < TimeSpan.Zero)
            config.ThrottleBackoffDelay = TimeSpan.Zero;
        if (config.ThrottleRecoveryInterval < TimeSpan.Zero)
            config.ThrottleRecoveryInterval = TimeSpan.Zero;
    }

    /// <summary>
    /// AIMD, the same shape TCP uses for congestion: a throttle signal halves the connection
    /// ceiling at once and pauses every worker, and the ceiling only creeps back one connection at
    /// a time after the origin has stayed quiet. Reacting per segment instead leaves the other
    /// workers hammering the limiter that just fired, which is what re-trips it.
    /// </summary>
    private sealed class ThrottleController(
        int initialConcurrency,
        int floor,
        TimeSpan backoffDelay,
        TimeSpan recoveryInterval,
        Func<int> getConcurrency,
        Action<int> setConcurrency)
    {
        private readonly Lock _lock = new();
        private readonly int _floor = Math.Max(floor, 1);
        private int _ceiling = initialConcurrency;
        private long _resumeAt;
        private long _lastTripAt = Stopwatch.GetTimestamp();

        /// <summary>Upper bound the throughput-based adapter must not climb past.</summary>
        public int Ceiling
        {
            get
            {
                lock (_lock)
                    return _ceiling;
            }
        }

        public void Trip(TimeSpan? retryAfter)
        {
            var pause = retryAfter is { } supplied && supplied > backoffDelay ? supplied : backoffDelay;
            var now = Stopwatch.GetTimestamp();

            lock (_lock)
            {
                _lastTripAt = now;

                var deadline = now + (long)(pause.TotalSeconds * Stopwatch.Frequency);
                if (deadline > _resumeAt)
                    _resumeAt = deadline;

                var reduced = Math.Max(_ceiling / 2, _floor);
                if (reduced >= _ceiling)
                    return;

                _ceiling = reduced;
            }

            if (getConcurrency() > Ceiling)
                setConcurrency(Ceiling);
        }

        /// <summary>Called on the adapt tick: one connection back per quiet interval.</summary>
        public void Recover(int hardCeiling)
        {
            if (recoveryInterval <= TimeSpan.Zero)
                return;

            lock (_lock)
            {
                if (_ceiling >= hardCeiling ||
                    Stopwatch.GetElapsedTime(_lastTripAt) < recoveryInterval)
                    return;

                _ceiling++;
                _lastTripAt = Stopwatch.GetTimestamp();
            }
        }

        public async Task WaitAsync(CancellationToken ct)
        {
            while (true)
            {
                TimeSpan remaining;
                lock (_lock)
                {
                    var now = Stopwatch.GetTimestamp();
                    if (_resumeAt <= now)
                        return;

                    remaining = TimeSpan.FromSeconds((_resumeAt - now) / (double)Stopwatch.Frequency);
                }

                await Task.Delay(remaining, ct).ConfigureAwait(false);
            }
        }
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

    /// <summary>Holds the first fatal error so it can be rethrown instead of the cancellations it caused.</summary>
    private sealed class FailureState
    {
        private ExceptionDispatchInfo? _first;

        public bool TrySet(Exception exception) =>
            Interlocked.CompareExchange(ref _first, ExceptionDispatchInfo.Capture(exception), null) is null;

        public void ThrowIfFailed() => Volatile.Read(ref _first)?.Throw();
    }

    private readonly record struct DownloadRange(long Start, long End);

    private readonly record struct RequestTarget(Uri Uri, IReadOnlyDictionary<string, string>? Headers, bool IsDirect);

    private sealed record ProbeResult(
        string FileName,
        long Size,
        string? ContentType,
        bool SupportsRange,
        Uri DownloadUri,
        string? ETag,
        string? IfRange,
        string? LastModified)
    {
        public LightDownloadFileInfo CreateFileInfo(string destinationPath) => new()
        {
            FileName = FileName,
            FilePath = destinationPath,
            Size = Size,
            ContentType = ContentType,
            SupportsRange = SupportsRange
        };
    }

    private sealed class DownloadTarget
    {
        private readonly Uri _originalUri;
        private readonly Uri _directUri;
        private readonly IReadOnlyDictionary<string, string>? _originalHeaders;
        private readonly IReadOnlyDictionary<string, string>? _directHeaders;
        private readonly bool _hasDirectUri;
        private int _useFallback;

        public DownloadTarget(Uri originalUri, Uri directUri, IReadOnlyDictionary<string, string>? headers)
        {
            _originalUri = originalUri;
            _directUri = directUri;
            _originalHeaders = headers;
            _directHeaders = HaveSameAuthority(originalUri, directUri) ? headers : WithoutSensitiveHeaders(headers);
            _hasDirectUri = originalUri != directUri;
        }

        public RequestTarget GetRequestTarget()
        {
            return _hasDirectUri && Volatile.Read(ref _useFallback) == 0
                ? new RequestTarget(_directUri, _directHeaders, true)
                : new RequestTarget(_originalUri, _originalHeaders, false);
        }

        public void Fallback()
        {
            Volatile.Write(ref _useFallback, 1);
        }
    }

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

        public string? ETag { get; init; }

        public string? LastModified { get; init; }

        public List<CompletedRange> CompletedRanges { get; set; } = completedRanges;
    }

    /// <summary>A transient segment failure: the range can be requeued and retried.</summary>
    private sealed class SegmentRetryException(
        long nextStart,
        string message,
        TimeSpan? retryAfter = null,
        Exception? innerException = null,
        bool throttled = false)
        : Exception(message, innerException)
    {
        public SegmentRetryException(long nextStart, string message, Exception? innerException)
            : this(nextStart, message, null, innerException)
        {
        }

        public long NextStart { get; } = nextStart;

        public TimeSpan? RetryAfter { get; } = retryAfter;

        /// <summary>The origin said "too often", not "something broke": back the whole download off.</summary>
        public bool Throttled { get; } = throttled;
    }

    /// <summary>A permanent failure: retrying cannot help, so the whole download stops now.</summary>
    private sealed class FatalDownloadException(
        string message,
        Exception? innerException = null,
        bool discardPartialData = false)
        : Exception(message, innerException)
    {
        public FatalDownloadException(string message, bool discardPartialData)
            : this(message, null, discardPartialData)
        {
        }

        public bool DiscardPartialData { get; } = discardPartialData;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _http.Dispose();
    }
}
