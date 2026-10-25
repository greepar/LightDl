using System.Diagnostics;
using System.Net;
using System.Net.Quic;
using LightDl;

if (args.Length < 1)
{
    Console.WriteLine("Usage: LightDl.Benchmark <url> [2|3] [workers=24] [seconds=30] [connections=1] [native|diagnostic]");
    return 2;
}
var protocol = args.Length > 1 ? int.Parse(args[1]) : 3;
var workers = args.Length > 2 ? int.Parse(args[2]) : 24;
var seconds = args.Length > 3 ? int.Parse(args[3]) : 30;
var connections = args.Length > 4 ? int.Parse(args[4]) : 1;
var native = args.Length > 5 && args[5] == "native";
if (protocol is not (2 or 3) || workers < 1 || seconds < 1 || connections < 0 || (!native && connections == 0))
    throw new ArgumentException("Protocol must be 2 or 3; workers and seconds must be positive. Zero connections requires native mode (automatic).");
Console.WriteLine($"runtime={Environment.Version} quic={QuicConnection.IsSupported} protocol={protocol} workers={workers} connections={connections}");
var directory = Directory.CreateTempSubdirectory("lightdl-benchmark-").FullName;
var timer = Stopwatch.StartNew();
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
var downloaded = 0L;
var retries = 0;
var responses = 0;
var config = new LightDownloadConfig
{
    ForceHttp3 = protocol == 3,
    EnableHttp3 = false,
    Http3ConnectionCount = connections,
    ChunkCount = workers,
    EnableResume = false,
    MaxRetry = 2,
    ProgressIntervalMs = 1000,
    HttpMessageHandlerFactory = native ? null : () => new ObservingHandler(protocol, connections, message =>
    {
        if (Interlocked.Increment(ref responses) <= 8)
            Console.WriteLine(message);
    }),
    RetryHandler = retry => { Interlocked.Increment(ref retries); Console.WriteLine($"retry={retry.Attempt} error={retry.Error.Message}"); }
};
try
{
    using var downloader = new LightDownloader(config);
    var request = LightDownloadRequest.ToFile(args[0], Path.Combine(directory, "payload"))
        .OnFileInfo(info => Console.WriteLine($"size={info.Size} ranges={info.SupportsRange}"))
        .OnProgress(progress =>
        {
            Interlocked.Exchange(ref downloaded, progress.DownloadedBytes);
            Console.WriteLine($"t={timer.Elapsed.TotalSeconds:F1} bytes={progress.DownloadedBytes} MiB/s={progress.Speed / 1048576:F3} workers={progress.ActiveWorkers}");
        });
    var result = await downloader.DownloadAsync(request, timeout.Token);
    downloaded = result.Size;
    Console.WriteLine("completed=true");
}
catch (OperationCanceledException) when (timeout.IsCancellationRequested)
{
    Console.WriteLine("time_budget_reached=true");
}
catch (Exception error)
{
    Console.WriteLine(error);
    return 1;
}
finally
{
    Console.WriteLine($"elapsed={timer.Elapsed.TotalSeconds:F3} observed_bytes={downloaded} retries={retries} responses={responses}");
    Directory.Delete(directory, recursive: true);
}
return 0;

sealed class ObservingHandler : HttpMessageHandler
{
    private readonly HttpMessageInvoker[] _transports;
    private readonly int _protocol;
    private readonly Action<string> _log;
    private int _next;

    public ObservingHandler(int protocol, int connections, Action<string> log)
    {
        _protocol = protocol;
        _log = log;
        _transports = Enumerable.Range(0, connections).Select(_ => new HttpMessageInvoker(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(10)
        })).ToArray();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Version = _protocol == 3 ? HttpVersion.Version30 : HttpVersion.Version20;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        var index = (uint)Interlocked.Increment(ref _next) % (uint)_transports.Length;
        var response = await _transports[index].SendAsync(request, ct);
        _log($"HTTP/{response.Version} status={(int)response.StatusCode} host={request.RequestUri!.Host} range={request.Headers.Range} content-range={response.Content.Headers.ContentRange} length={response.Content.Headers.ContentLength} cf-ray={string.Join(',', response.Headers.TryGetValues("cf-ray", out var rays) ? rays : [])}");
        return response;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            foreach (var transport in _transports) transport.Dispose();
        base.Dispose(disposing);
    }
}
