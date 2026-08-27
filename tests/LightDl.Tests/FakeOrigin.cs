using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace LightDl.Tests;

/// <summary>
/// An in-memory HTTP origin that can be told to misbehave the way real servers do: truncate
/// responses, refuse ranges, fail a specific range, throttle, or change its content mid-download.
/// </summary>
public sealed class FakeOrigin
{
    private readonly List<RequestRecord> _requests = [];
    private readonly Lock _lock = new();
    private readonly Dictionary<int, long> _peakReachedAt = [];
    private int _concurrentRequests;
    private int _peakConcurrentRequests;

    public FakeOrigin(byte[] content)
    {
        Content = content;
    }

    public byte[] Content { get; set; }

    /// <summary>Serves 200 without Accept-Ranges, forcing the single-stream path.</summary>
    public bool SupportsRange { get; set; } = true;

    /// <summary>Omits Content-Length so the size is unknown.</summary>
    public bool OmitContentLength { get; set; }

    public string? ETag { get; set; }

    public DateTimeOffset? LastModified { get; set; }

    /// <summary>Sends only this fraction of each body, then ends the stream early.</summary>
    public double TruncateBodyRatio { get; set; } = 1.0;

    /// <summary>Number of responses to truncate before behaving normally. -1 truncates forever.</summary>
    public int TruncateCount { get; set; }

    /// <summary>Status returned for any range starting at this offset.</summary>
    public (long Start, HttpStatusCode Status)? FailRangeAt { get; set; }

    /// <summary>Status returned for every request until the counter runs out.</summary>
    public (int Count, HttpStatusCode Status, TimeSpan? RetryAfter)? FailFirstRequests { get; set; }

    /// <summary>Delay applied while streaming each body.</summary>
    public TimeSpan BodyDelay { get; set; }

    /// <summary>Extra per-chunk delay applied only to ranges at or past this offset.</summary>
    public (long From, TimeSpan Delay)? SlowTailFrom { get; set; }

    /// <summary>Status returned for requests carrying this If-Range value, simulating an expired signed URL.</summary>
    public (int AfterRequests, HttpStatusCode Status)? ExpireAfter { get; set; }

    /// <summary>
    /// Answers 200 with a small HTML page instead of the requested range, the way a rate-limit or
    /// anti-bot gate does. Starts once <c>AfterRequests</c> requests have been seen and lasts for
    /// <c>Count</c> responses.
    /// </summary>
    public (int AfterRequests, int Count)? GatePage { get; set; }

    /// <summary>Answers 429 for <c>Count</c> requests once <c>AfterRequests</c> have been seen.</summary>
    public (int AfterRequests, int Count)? ThrottleAfter { get; set; }

    public IReadOnlyList<RequestRecord> Requests
    {
        get
        {
            lock (_lock)
                return _requests.ToList();
        }
    }

    public int PeakConcurrentRequests
    {
        get
        {
            lock (_lock)
                return _peakConcurrentRequests;
        }
    }

    /// <summary>Stopwatch timestamp when this many requests were first in flight at once, if ever.</summary>
    public long? PeakReachedAt(int concurrency)
    {
        lock (_lock)
            return _peakReachedAt.TryGetValue(concurrency, out var timestamp) ? timestamp : null;
    }

    public LightDownloadConfig Configure(LightDownloadConfig config)
    {
        config.HttpMessageHandlerFactory = () => new Handler(this);
        return config;
    }

    private async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var range = request.Headers.Range?.Ranges.FirstOrDefault();
        var ifRange = request.Headers.TryGetValues("If-Range", out var values) ? values.FirstOrDefault() : null;
        var acceptEncoding = request.Headers.AcceptEncoding.ToString();

        lock (_lock)
        {
            _requests.Add(new RequestRecord(range?.From, range?.To, ifRange, acceptEncoding));
            _concurrentRequests++;
            if (_concurrentRequests > _peakConcurrentRequests)
            {
                _peakConcurrentRequests = _concurrentRequests;
                _peakReachedAt[_concurrentRequests] = Stopwatch.GetTimestamp();
            }
        }

        HttpResponseMessage response;
        try
        {
            response = await BuildResponseAsync(request, range, ifRange, ct).ConfigureAwait(false);
        }
        catch
        {
            Release();
            throw;
        }

        // A connection is open until its body has been read, not until its headers exist. Counting
        // only the latter makes every concurrency assertion a race against instant local responses.
        if (response.Content is StreamContent)
        {
            var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var tracked = new StreamContent(new TrackedStream(body, Release));
            foreach (var header in response.Content.Headers)
                tracked.Headers.TryAddWithoutValidation(header.Key, header.Value);

            response.Content = tracked;
        }
        else
        {
            Release();
        }

        return response;
    }

    private void Release()
    {
        lock (_lock)
            _concurrentRequests--;
    }

    /// <summary>Holds a connection slot open until the caller finishes reading the body.</summary>
    private sealed class TrackedStream(Stream inner, Action onClosed) : Stream
    {
        private int _closed;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => inner.ReadAsync(buffer, ct);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => inner.ReadAsync(buffer, offset, count, ct);

        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _closed, 1) == 0)
            {
                inner.Dispose();
                onClosed();
            }

            base.Dispose(disposing);
        }
    }

    private async Task<HttpResponseMessage> BuildResponseAsync(HttpRequestMessage request,
        RangeItemHeaderValue? range, string? ifRange, CancellationToken ct)
    {
        var content = Content;

        if (FailFirstRequests is { } failing && failing.Count > 0)
        {
            FailFirstRequests = (failing.Count - 1, failing.Status, failing.RetryAfter);
            var failure = new HttpResponseMessage(failing.Status) { RequestMessage = request };
            if (failing.RetryAfter is { } retryAfter)
                failure.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);

            return failure;
        }

        if (range is not null && FailRangeAt is { } failRange && range.From == failRange.Start)
            return new HttpResponseMessage(failRange.Status) { RequestMessage = request };

        if (ThrottleAfter is { } limit)
        {
            var reject = false;
            lock (_lock)
            {
                if (limit.Count > 0 && _requests.Count > limit.AfterRequests)
                {
                    ThrottleAfter = (limit.AfterRequests, limit.Count - 1);
                    reject = true;
                }
            }

            if (reject)
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests) { RequestMessage = request };
        }

        if (GatePage is { } gate)
        {
            var serveGate = false;
            lock (_lock)
            {
                if (gate.Count > 0 && _requests.Count > gate.AfterRequests)
                {
                    GatePage = (gate.AfterRequests, gate.Count - 1);
                    serveGate = true;
                }
            }

            if (serveGate)
            {
                var page = "<html><head><title>Security check</title></head><body>hold on</body></html>"u8.ToArray();
                var gated = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new ByteArrayContent(page)
                };
                gated.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html") { CharSet = "utf-8" };
                return gated;
            }
        }

        if (ExpireAfter is { } expiry)
        {
            lock (_lock)
            {
                if (_requests.Count > expiry.AfterRequests)
                    return new HttpResponseMessage(expiry.Status) { RequestMessage = request };
            }
        }

        // If-Range with a stale validator must fall back to a full 200 response.
        var validatorStale = ifRange is not null && ETag is not null && ifRange != ETag;

        if (range is null || !SupportsRange || validatorStale)
        {
            var body = await BuildBodyAsync(content, ct).ConfigureAwait(false);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StreamContent(body)
            };
            if (!OmitContentLength)
                response.Content.Headers.ContentLength = content.Length;
            if (!SupportsRange)
                response.Headers.AcceptRanges.Add("none");

            ApplyValidators(response);
            return response;
        }

        var from = range.From ?? 0;
        var to = Math.Min(range.To ?? content.Length - 1, content.Length - 1);
        var length = (int)(to - from + 1);
        var slice = new byte[length];
        Array.Copy(content, from, slice, 0, length);

        var tailDelay = SlowTailFrom is { } tail && from >= tail.From ? tail.Delay : TimeSpan.Zero;
        var partial = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            RequestMessage = request,
            Content = new StreamContent(tailDelay > TimeSpan.Zero
                ? new SlowStream(slice, tailDelay, 8 * 1024)
                : await BuildBodyAsync(slice, ct).ConfigureAwait(false))
        };
        partial.Content.Headers.ContentLength = length;
        partial.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, content.Length);
        partial.Headers.AcceptRanges.Add("bytes");
        ApplyValidators(partial);
        return partial;
    }

    private void ApplyValidators(HttpResponseMessage response)
    {
        if (ETag is not null)
            response.Headers.ETag = new EntityTagHeaderValue(ETag);
        if (LastModified is not null)
            response.Content.Headers.LastModified = LastModified;
    }

    private Task<Stream> BuildBodyAsync(byte[] body, CancellationToken ct)
    {
        var truncate = TruncateCount != 0 && body.Length > 1;
        if (truncate && TruncateCount > 0)
            TruncateCount--;

        var sent = truncate ? (int)(body.Length * TruncateBodyRatio) : body.Length;

        // A truncated body still declares the full Content-Length, exactly like a dropped connection.
        if (BodyDelay <= TimeSpan.Zero)
            return Task.FromResult<Stream>(new MemoryStream(body, 0, sent, writable: false));

        var slice = new byte[sent];
        Array.Copy(body, slice, sent);
        return Task.FromResult<Stream>(new SlowStream(slice, BodyDelay));
    }

    public sealed record RequestRecord(long? From, long? To, string? IfRange, string AcceptEncoding)
    {
        /// <summary>Stopwatch timestamp of arrival, for asserting on when connections opened.</summary>
        public long Timestamp { get; init; } = Stopwatch.GetTimestamp();
    }

    private sealed class Handler(FakeOrigin origin) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => origin.HandleAsync(request, ct);
    }
}
