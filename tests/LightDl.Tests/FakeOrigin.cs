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
            _peakConcurrentRequests = Math.Max(_peakConcurrentRequests, _concurrentRequests);
        }

        try
        {
            return await BuildResponseAsync(request, range, ifRange, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_lock)
                _concurrentRequests--;
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

        var partial = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            RequestMessage = request,
            Content = new StreamContent(await BuildBodyAsync(slice, ct).ConfigureAwait(false))
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

    public sealed record RequestRecord(long? From, long? To, string? IfRange, string AcceptEncoding);

    private sealed class Handler(FakeOrigin origin) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => origin.HandleAsync(request, ct);
    }
}
