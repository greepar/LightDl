using System.Net;
using System.Net.Quic;
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

var builder = WebApplication.CreateSlimBuilder();
builder.WebHost.UseQuic();
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen =>
{
    listen.Protocols = HttpProtocols.Http3;
    listen.UseHttps(certificate);
}));
await using var app = builder.Build();
var payload = new byte[1024 * 1024];
new Random(42).NextBytes(payload);
var expectedHash = Convert.ToHexString(SHA256.HashData(payload));
var requests = 0;
var rangeRequests = 0;
app.Run(async context =>
{
    if (context.Request.Protocol != "HTTP/3")
        throw new InvalidOperationException($"Unexpected protocol: {context.Request.Protocol}");
    Interlocked.Increment(ref requests);
    if (context.Request.Headers.ContainsKey("Range"))
        Interlocked.Increment(ref rangeRequests);
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
}
finally
{
    await app.StopAsync();
    Directory.Delete(directory, recursive: true);
}
