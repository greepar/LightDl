using System.Collections.Concurrent;
using System.Net;
using System.Net.Quic;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LightDl;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;

Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}; RID: {RuntimeInformation.RuntimeIdentifier}");
if (!QuicConnection.IsSupported)
    throw new InvalidOperationException("Static MsQuic was not loaded: QuicConnection.IsSupported=false.");
if (Directory.EnumerateFiles(AppContext.BaseDirectory).Any(path =>
        Path.GetFileName(path).Contains("msquic", StringComparison.OrdinalIgnoreCase)))
    throw new InvalidOperationException("A shared MsQuic sidecar was published.");

using var key = RSA.Create(2048);
var certificateRequest = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
var san = new SubjectAlternativeNameBuilder();
san.AddDnsName("localhost");
san.AddIpAddress(IPAddress.Loopback);
certificateRequest.CertificateExtensions.Add(san.Build());
certificateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
using var generatedCertificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
// Schannel needs an import with a key provider/container rather than the
// ephemeral CNG key attached by CertificateRequest.CreateSelfSigned.
using var certificate = X509CertificateLoader.LoadPkcs12(
    generatedCertificate.Export(X509ContentType.Pfx), null,
    X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);

// Kestrel cannot use port 0 with TCP and QUIC on the same endpoint.
var port = FindTcpAndUdpPort();
var builder = WebApplication.CreateSlimBuilder();
builder.WebHost.UseQuic();
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port, listen =>
{
    listen.Protocols = HttpProtocols.Http1AndHttp2AndHttp3;
    listen.UseHttps(certificate);
}));
await using var app = builder.Build();
var payload = new byte[1024 * 1024];
new Random(42).NextBytes(payload);
var expectedHash = Convert.ToHexString(SHA256.HashData(payload));
var requests = 0;
var rangeRequests = 0;
var decisions = new ConcurrentDictionary<string, int>();
var observations = new ConcurrentQueue<(string Path, string Protocol, bool AfterSelection)>();
app.Run(async context =>
{
    var path = context.Request.Path.Value!;
    var afterSelection = decisions.ContainsKey(path);
    observations.Enqueue((path, context.Request.Protocol, afterSelection));
    if (path == "/payload" && context.Request.Protocol != "HTTP/3")
        throw new InvalidOperationException($"Unexpected protocol: {context.Request.Protocol}");
    Interlocked.Increment(ref requests);
    if (context.Request.Headers.ContainsKey("Range"))
        Interlocked.Increment(ref rangeRequests);
    if (path == "/h3-unavailable" && context.Request.Protocol == "HTTP/3")
    {
        context.Abort();
        return;
    }
    var range = context.Request.GetTypedHeaders().Range?.Ranges.SingleOrDefault();
    if (path == "/h3-fails" && afterSelection && context.Request.Protocol == "HTTP/3" && range?.From is { } start && range.To is { } end)
    {
        context.Response.StatusCode = 206;
        context.Response.ContentType = "application/octet-stream";
        context.Response.ContentLength = end - start + 1;
        context.Response.Headers.ContentRange = $"bytes {start}-{end}/{payload.Length}";
        await context.Response.StartAsync();
        await context.Response.Body.WriteAsync(payload.AsMemory((int)start, (int)Math.Min(8192, end - start + 1)));
        await context.Response.Body.FlushAsync();
        context.Abort();
        return;
    }
    var h3Faster = path is "/h3-faster" or "/h3-fails";
    var slow = path == "/h2-faster" ? context.Request.Protocol == "HTTP/3" : h3Faster && context.Request.Protocol == "HTTP/2";
    if (slow && range?.To > 0)
        await Task.Delay(1000, context.RequestAborted);
    await Results.File(payload, "application/octet-stream", enableRangeProcessing: true).ExecuteAsync(context);
});
await app.StartAsync();
var origin = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
var directory = Directory.CreateTempSubdirectory("lightdl-quic-smoke-").FullName;
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
try
{
    // The static loader patch must not bypass normal certificate validation.
    using (var strict = new LightDownloader(new LightDownloadConfig { ForceHttp3 = true, MaxRetry = 0 }))
    {
        try
        {
            await strict.DownloadAsync(LightDownloadRequest.ToFile(origin + "/payload", Path.Combine(directory, "untrusted.bin")), timeout.Token);
            throw new InvalidOperationException("An untrusted server certificate was accepted.");
        }
        catch (LightDownloadException ex) when (ex.ToString().Contains("certificate", StringComparison.OrdinalIgnoreCase) ||
                                               ex.ToString().Contains("TLS", StringComparison.OrdinalIgnoreCase) ||
                                               ex.ToString().Contains("Authentication", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("PASS: default certificate validation rejects the self-signed server.");
        }
    }
    var file = Path.Combine(directory, "payload.bin");
    using var downloader = new LightDownloader(new LightDownloadConfig
    {
        ForceHttp3 = true,
        IgnoreSslErrors = true, // Only this local test uses a self-signed certificate.
        ChunkCount = 4,
        ConnectionRampUpDelay = TimeSpan.Zero,
        ChecksumAlgorithm = LightDownloadChecksumAlgorithm.Sha256,
        ExpectedChecksum = expectedHash,
        MaxRetry = 0
    });
    var result = await downloader.DownloadAsync(LightDownloadRequest.ToFile(origin + "/payload", file), timeout.Token);
    var actualHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(result.FilePath, timeout.Token)));
    if (actualHash != expectedHash || requests < 2 || rangeRequests < 2)
        throw new InvalidOperationException($"Download verification failed: requests={requests}, ranges={rangeRequests}, SHA256={actualHash}");
    Console.WriteLine($"PASS: static Native AOT HTTP/3 download, {payload.Length} bytes, {requests} requests, {rangeRequests} range requests, SHA256={actualHash}");

    foreach (var (path, winner) in new[] { ("/h3-faster", 3), ("/h2-faster", 2), ("/h3-unavailable", 2), ("/h3-fails", 2) })
    {
        var measured = false;
        var fellBack = false;
        using var automatic = new LightDownloader(new LightDownloadConfig
        {
            IgnoreSslErrors = true,
            ChunkCount = 4,
            Http3ProbeSize = 64 * 1024,
            MaxRetry = 0,
            ConnectionRampUpDelay = TimeSpan.Zero,
            ChecksumAlgorithm = LightDownloadChecksumAlgorithm.Sha256,
            ExpectedChecksum = expectedHash,
            ProtocolSelectionHandler = selection =>
            {
                measured |= !selection.IsFallback;
                fellBack |= selection.IsFallback;
                decisions[path] = selection.SelectedVersion.Major;
                Console.WriteLine($"Decision {path}: H{selection.SelectedVersion.Major}, H2={selection.Http2Speed}, H3={selection.Http3Speed}, fallback={selection.IsFallback}");
            }
        });
        var output = Path.Combine(directory, path[1..] + ".bin");
        var automaticResult = await automatic.DownloadAsync(LightDownloadRequest.ToFile(origin + path, output), timeout.Token);
        if (!measured || automatic.SelectedHttpVersion?.Major != winner ||
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(automaticResult.FilePath, timeout.Token))) != expectedHash)
            throw new InvalidOperationException($"Automatic protocol selection or data integrity failed for {path}.");
        var later = observations.Where(item => item.Path == path && item.AfterSelection).ToArray();
        if (path == "/h3-fails")
        {
            if (!fellBack || !later.Any(item => item.Protocol == "HTTP/2"))
                throw new InvalidOperationException("A failing selected H3 connection did not recover over H2.");
        }
        else if (later.Length == 0 || later.Any(item => item.Protocol != $"HTTP/{winner}"))
            throw new InvalidOperationException($"The selected protocol was not retained for {path}.");
        Console.WriteLine($"PASS: {path}, final H{winner}, SHA256 verified, post-selection requests={later.Length}");
    }

}
finally
{
    await app.StopAsync();
    Directory.Delete(directory, recursive: true);
}

static int FindTcpAndUdpPort()
{
    for (var attempt = 0; attempt < 20; attempt++)
    {
        using var tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        tcp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)tcp.LocalEndPoint!).Port;
        try
        {
            udp.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return port;
        }
        catch (SocketException) { }
    }
    throw new InvalidOperationException("No available local TCP/UDP port found.");
}
