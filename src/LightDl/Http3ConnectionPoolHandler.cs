using System.Net;

namespace LightDl;

// SocketsHttpHandler multiplexes all range workers onto one QUIC connection. More streams
// cannot increase that connection's congestion window, and loss slows every worker on it.
// Separate pools let bulk ranges make independent progress. Metadata, single-stream transfers,
// and H2 keep the primary pool; extra pools establish connections only when H3 ranges use them.
internal sealed class Http3ConnectionPoolHandler : HttpMessageHandler
{
    private readonly HttpMessageInvoker[] _pools;
    private int _next = -1;
    private int _activePoolCount;

    public Http3ConnectionPoolHandler(Func<SocketsHttpHandler> createHandler, int count)
    {
        _pools = new HttpMessageInvoker[count];
        _activePoolCount = count;
        for (var i = 0; i < count; i++)
            _pools[i] = new HttpMessageInvoker(createHandler());
    }

    // Restrict new requests without tearing down streams already using other pools.
    public void SetConcurrency(int concurrency) =>
        Volatile.Write(ref _activePoolCount, Math.Clamp(concurrency, 1, _pools.Length));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var index = 0;
        if (request.Version == HttpVersion.Version30 &&
            request.Headers.Range?.Ranges.FirstOrDefault() is { From: { } start, To: { } end } && end > start)
        {
            index = (int)((uint)Interlocked.Increment(ref _next) % (uint)Volatile.Read(ref _activePoolCount));
        }
        return _pools[index].SendAsync(request, ct);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            foreach (var pool in _pools)
                pool.Dispose();
        base.Dispose(disposing);
    }
}
