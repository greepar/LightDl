using System.Net;
using Xunit;

namespace LightDl.Tests;

public sealed class Http3Tests
{
    [Fact]
    public async Task Http3_Is_Required_For_Probes_Redirects_And_Downloads()
    {
        using var directory = new TestDirectory();
        var requests = new List<(Version Version, HttpVersionPolicy Policy, string Host)>();
        using var downloader = new LightDownloader(new LightDownloadConfig
        {
            ForceHttp3 = true,
            EnableHttp3 = false,
            ChunkCount = 1,
            CheckFreeSpace = false,
            HttpMessageHandlerFactory = () => new Handler(request =>
            {
                requests.Add((request.Version, request.VersionPolicy, request.RequestUri!.Host));
                return request.RequestUri.Host == "origin.test"
                    ? Redirect("https://cdn.test/file")
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            })
        });
        var file = Path.Combine(directory.Path, "file");
        await downloader.DownloadAsync(LightDownloadRequest.ToFile("https://origin.test/file", file));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(file));
        Assert.True(requests.Count >= 4); // Both probe and body follow the redirect.
        Assert.All(requests, r =>
        {
            Assert.Equal(HttpVersion.Version30, r.Version);
            Assert.Equal(HttpVersionPolicy.RequestVersionExact, r.Policy);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Http3_Rejects_Plain_Http_Including_Redirects(bool redirect)
    {
        using var directory = new TestDirectory();
        var requests = 0;
        using var downloader = new LightDownloader(new LightDownloadConfig
        {
            ForceHttp3 = true,
            MaxRetry = 0,
            HttpMessageHandlerFactory = () => new Handler(_ =>
            {
                requests++;
                return Redirect("http://insecure.test/file");
            })
        });
        var error = await Assert.ThrowsAsync<LightDownloadException>(() => downloader.DownloadAsync(
            LightDownloadRequest.ToFile(redirect ? "https://origin.test/file" : "http://origin.test/file",
                Path.Combine(directory.Path, "file"))));
        Assert.Contains("HTTPS", error.ToString());
        Assert.Equal(redirect ? 1 : 0, requests);
    }

    private static HttpResponseMessage Redirect(string url) => new(HttpStatusCode.Redirect)
    {
        Headers = { Location = new Uri(url) }
    };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(send(request));
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("lightdl-http3-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
