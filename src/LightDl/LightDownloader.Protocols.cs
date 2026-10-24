using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Quic;

namespace LightDl;

public sealed partial class LightDownloader
{
    private const long MinimumProtocolRangeSize = 256 * 1024;
    private bool CanProbeHttp3(Uri uri)
    {
        if (!_config.EnableHttp3 || _config.UseProxy || uri.Scheme != Uri.UriSchemeHttps)
            return false;
        // Custom transports declare their own capabilities; also permits deterministic tests.
        if (_config.HttpMessageHandlerFactory is not null)
            return true;
        return (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) &&
               QuicConnection.IsSupported;
    }

    private static bool IsProtocolTransportFailure(Exception? exception) => exception switch
    {
        HttpRequestException error => error.HttpRequestError is
            HttpRequestError.Unknown or HttpRequestError.VersionNegotiationError or
            HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.HttpProtocolError or HttpRequestError.ResponseEnded,
        IOException or OperationCanceledException or PlatformNotSupportedException => true,
        _ => false
    };

    private async Task CompareProtocolsAsync(
        DownloadTarget target, DownloadSegment first, DownloadSegment second,
        string path, long totalLength, ProbeResult probe, Stopwatch transferStopwatch,
        Action<long> onBytes, Func<long> getDownloaded, Action<long, long> onCompleted,
        ConcurrentQueue<DownloadSegment> retryQueue, ThrottleController? throttle, CancellationToken ct)
    {
        using var samplesCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task<double?> SampleAsync(DownloadSegment segment, Version version)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(samplesCts.Token);
            budget.CancelAfter(_config.Http3ProbeTimeout);
            var bytes = 0L;
            var timer = Stopwatch.StartNew();
            try
            {
                await DownloadChunkAsync(target, path, segment.Start, segment.End,
                    count => { bytes += count; onBytes(count); }, getDownloaded,
                    () => 1, () => _config.NoDataTimeout,
                    totalLength, transferStopwatch, probe.IfRange, probe.ContentType, onCompleted,
                    budget.Token, probeVersion: version).ConfigureAwait(false);
                return bytes / Math.Max(timer.Elapsed.TotalSeconds, 0.000001);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // DownloadChunkAsync committed bytes written before the budget expired.
                RequeueRemainder();
                return null;
            }
            catch (SegmentRetryException error)
            {
                if (bytes > 0)
                    onCompleted(segment.Start, segment.Start + bytes - 1);
                if (error.Throttled)
                    throttle?.Trip(error.RetryAfter);
                RequeueRemainder();
                return null;
            }
            catch
            {
                await samplesCts.CancelAsync().ConfigureAwait(false);
                throw;
            }

            void RequeueRemainder()
            {
                if (segment.Start + bytes <= segment.End)
                    retryQueue.Enqueue(new DownloadSegment(segment.Start + bytes, segment.End, 0));
            }
        }

        var h2Task = SampleAsync(first, HttpVersion.Version20);
        double? h2;
        double? h3;
        if (_config.ChunkCount == 1)
        {
            h2 = await h2Task.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            h3 = await SampleAsync(second, HttpVersion.Version30).ConfigureAwait(false);
        }
        else
        {
            var h3Task = SampleAsync(second, HttpVersion.Version30);
            // Observe both tasks, even when an entity-validation or storage error is fatal.
            await Task.WhenAll(h2Task, h3Task).ConfigureAwait(false);
            h2 = await h2Task.ConfigureAwait(false);
            h3 = await h3Task.ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
        var selected = h3 is not null && (h2 is null || h3 > h2) ? HttpVersion.Version30 :
            h2 is not null ? HttpVersion.Version20 :
            probe.HttpVersion == HttpVersion.Version30 ? HttpVersion.Version30 : probe.HttpVersion;
        target.SelectProtocol(selected, h2, h3);
    }
}
