using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Xunit;

namespace LightDl.Tests;

public sealed class ProtocolSelectionTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("lightdl-protocol-").FullName;
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private LightDownloadConfig Config(Origin origin) => new()
    {
        HttpMessageHandlerFactory = () => origin,
        ChunkCount = 2,
        SegmentSize = 64 * 1024,
        MinSegmentSize = 64 * 1024,
        MaxSegmentSize = 64 * 1024,
        Http3ProbeSize = 64 * 1024,
        Http3ProbeTimeout = TimeSpan.FromSeconds(2),
        ConnectionRampUpDelay = TimeSpan.Zero,
        BufferSize = 8192,
        CheckFreeSpace = false,
        MaxRetry = 0
    };

    private Task<LightDownloadResult> Download(LightDownloader downloader, string file = "file", CancellationToken ct = default) =>
        downloader.DownloadAsync(LightDownloadRequest.ToFile("https://origin.test/file", Path.Combine(_directory, file)), ct);

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Faster_Protocol_Is_Locked_And_Sample_Bytes_Are_Reused(int winner)
    {
        var origin = new Origin { Winner = winner };
        LightDownloadProtocolSelection? decision = null;
        var config = Config(origin);
        config.ProtocolSelectionHandler = selection =>
        {
            decision = selection;
            origin.Selected = true;
        };
        using var downloader = new LightDownloader(config);
        var result = await Download(downloader);
        Assert.Equal(origin.Payload, await File.ReadAllBytesAsync(result.FilePath));
        Assert.NotNull(decision);
        Assert.Equal(winner, decision.SelectedVersion.Major);
        Assert.True(decision.Http2Speed > 0);
        Assert.True(decision.Http3Speed > 0);
        Assert.Equal(winner, downloader.SelectedHttpVersion!.Major);
        Assert.All(origin.Records.Where(r => r.AfterSelection), r => Assert.Equal(winner, r.Version.Major));
        Assert.Equal(origin.Payload.Length, origin.Records.Where(r => r.End != 0).Sum(r => r.End - r.Start + 1));
        Assert.All(origin.Records.Where(r => r.End != 0), r => Assert.Equal("\"entity\"", r.IfRange));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unavailable_Or_Timed_Out_H3_Does_Not_Consume_Retry_Budget(bool timeout)
    {
        var origin = new Origin { NoHttp3 = !timeout, BlockHttp3 = timeout };
        var config = Config(origin);
        config.Http3ProbeTimeout = TimeSpan.FromMilliseconds(100);
        LightDownloadProtocolSelection? decision = null;
        config.ProtocolSelectionHandler = selection => { decision = selection; origin.Selected = true; };
        using var downloader = new LightDownloader(config);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await Download(downloader, ct: cancellation.Token);
        Assert.Equal(origin.Payload, await File.ReadAllBytesAsync(result.FilePath));
        Assert.Equal(HttpVersion.Version20, downloader.SelectedHttpVersion);
        Assert.Null(decision!.Http3Speed);
        Assert.All(origin.Records.Where(r => r.AfterSelection), r => Assert.Equal(HttpVersion.Version20, r.Version));
    }

    [Fact]
    public async Task An_H3_Body_Failure_Switches_Protocol_And_Resumes_After_Written_Bytes()
    {
        var origin = new Origin { Winner = 3, FailHttp3Body = true };
        var fallback = false;
        var config = Config(origin);
        config.ProtocolSelectionHandler = selection =>
        {
            origin.Selected = true;
            fallback |= selection.IsFallback;
        };
        using var downloader = new LightDownloader(config);
        var result = await Download(downloader);
        Assert.Equal(origin.Payload, await File.ReadAllBytesAsync(result.FilePath));
        Assert.True(fallback);
        Assert.Equal(HttpVersion.Version20, downloader.SelectedHttpVersion);
        Assert.NotNull(origin.FailedStart);
        Assert.Contains(origin.Records, r => r.Version == HttpVersion.Version20 && r.Start == origin.FailedStart + 8192);
    }

    [Fact]
    public async Task A_Single_Worker_Still_Compares_Without_Opening_Concurrent_Requests()
    {
        var origin = new Origin { Winner = 3 };
        var config = Config(origin);
        config.ChunkCount = 1;
        using var downloader = new LightDownloader(config);
        await Download(downloader);
        Assert.Equal(HttpVersion.Version30, downloader.SelectedHttpVersion);
        Assert.Equal(1, origin.PeakRequests);
    }

    [Fact]
    public async Task Cancellation_Stops_Both_Samples_And_Leaves_Resumable_Data()
    {
        var origin = new Origin { BlockHttp3 = true };
        using var cancellation = new CancellationTokenSource();
        origin.OnBlocked = () => cancellation.Cancel();
        using (var downloader = new LightDownloader(Config(origin)))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Download(downloader, ct: cancellation.Token));
        origin.BlockHttp3 = false;
        origin.OnBlocked = null;
        using var resumed = new LightDownloader(Config(origin));
        var result = await Download(resumed);
        Assert.Equal(origin.Payload, await File.ReadAllBytesAsync(result.FilePath));
        Assert.Equal(0, origin.ActiveRequests);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Disabled_Auto_Or_Proxy_Uses_H2_Without_H3_Probes(bool enabled, bool proxy)
    {
        var origin = new Origin();
        var config = Config(origin);
        config.EnableHttp3 = enabled;
        config.UseProxy = proxy;
        using var downloader = new LightDownloader(config);
        await Download(downloader);
        Assert.DoesNotContain(origin.Records, r => r.Version == HttpVersion.Version30);
    }

    [Fact]
    public async Task Small_Files_Do_Not_Pay_For_Protocol_Comparison()
    {
        var origin = new Origin(1024);
        var config = Config(origin);
        config.ProtocolSelectionHandler = _ => throw new InvalidOperationException("Should not run");
        using var downloader = new LightDownloader(config);
        var result = await Download(downloader);
        Assert.Equal(origin.Payload, await File.ReadAllBytesAsync(result.FilePath));
        Assert.DoesNotContain(origin.Records, r => r.Version == HttpVersion.Version30);
    }

    [Fact]
    public async Task A_Selected_H3_Header_Failure_Falls_Back_With_MaxRetry_Zero()
    {
        var origin = new Origin { Winner = 3 };
        var config = Config(origin);
        var fallback = false;
        config.ProtocolSelectionHandler = selection =>
        {
            origin.Selected = true;
            origin.NoHttp3 = true;
            fallback |= selection.IsFallback;
        };
        using var downloader = new LightDownloader(config);
        var result = await Download(downloader);
        Assert.Equal(origin.Payload, await File.ReadAllBytesAsync(result.FilePath));
        Assert.True(fallback);
        Assert.Equal(HttpVersion.Version20, downloader.SelectedHttpVersion);
    }

    [Fact]
    public async Task A_New_Download_Repeats_Selection_Instead_Of_Keeping_The_Previous_Winner()
    {
        var origin = new Origin { Winner = 3 };
        using var downloader = new LightDownloader(Config(origin));
        await Download(downloader, "first");
        Assert.Equal(HttpVersion.Version30, downloader.SelectedHttpVersion);
        origin.Winner = 2;
        await Download(downloader, "second");
        Assert.Equal(HttpVersion.Version20, downloader.SelectedHttpVersion);
    }

    [Fact]
    public async Task An_Http1_Only_Origin_Remains_Downloadable()
    {
        var origin = new Origin { Http1Only = true };
        var config = Config(origin);
        config.ProtocolSelectionHandler = _ => origin.Selected = true;
        using var downloader = new LightDownloader(config);
        var result = await Download(downloader);
        Assert.Equal(origin.Payload, await File.ReadAllBytesAsync(result.FilePath));
        Assert.Equal(HttpVersion.Version11, downloader.SelectedHttpVersion);
        Assert.All(origin.Records.Where(r => r.AfterSelection), r => Assert.Equal(HttpVersion.Version11, r.Version));
    }

    [Fact]
    public async Task Forced_H3_Does_Not_Fall_Back_When_It_Is_Unavailable()
    {
        var origin = new Origin { NoHttp3 = true };
        var config = Config(origin);
        config.ForceHttp3 = true;
        using var downloader = new LightDownloader(config);
        await Assert.ThrowsAsync<LightDownloadException>(() => Download(downloader));
        Assert.All(origin.Records, r => Assert.Equal(HttpVersion.Version30, r.Version));
    }

    [Fact]
    public async Task An_H3_Only_Origin_Is_Discovered_When_The_Tcp_Probe_Fails()
    {
        var origin = new Origin { TcpUnavailable = true, Winner = 3 };
        using var downloader = new LightDownloader(Config(origin));
        var result = await Download(downloader);
        Assert.Equal(origin.Payload, await File.ReadAllBytesAsync(result.FilePath));
        Assert.Equal(HttpVersion.Version30, downloader.SelectedHttpVersion);
    }

    [Fact]
    public async Task An_Optional_Discovery_Timeout_Is_A_Download_Error_Not_User_Cancellation()
    {
        var origin = new Origin { TcpUnavailable = true, BlockHttp3 = true };
        var config = Config(origin);
        config.Http3ProbeTimeout = TimeSpan.FromMilliseconds(50);
        using var downloader = new LightDownloader(config);
        await Assert.ThrowsAsync<LightDownloadException>(() => Download(downloader));
    }

    private sealed record Record(Version Version, long Start, long End, string? IfRange, bool AfterSelection);

    private sealed class Origin(int length = 1024 * 1024) : HttpMessageHandler
    {
        private readonly Lock _lock = new();
        private readonly List<Record> _records = [];
        private int _active;
        private int _peak;
        private int _failed;
        public int Winner { get; set; } = 2;
        public bool TcpUnavailable { get; set; }
        public bool Http1Only { get; set; }
        public bool NoHttp3 { get; set; }
        public bool BlockHttp3 { get; set; }
        public bool FailHttp3Body { get; set; }
        public bool Selected { get; set; }
        public Action? OnBlocked { get; set; }
        public long? FailedStart { get; private set; }
        public byte[] Payload { get; } = Enumerable.Range(0, length).Select(i => (byte)(i * 31)).ToArray();
        public int PeakRequests => _peak;
        public int ActiveRequests => _active;
        public IReadOnlyList<Record> Records { get { lock (_lock) return _records.ToArray(); } }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var range = request.Headers.Range!.Ranges.Single();
            var start = range.From!.Value;
            var end = range.To!.Value;
            lock (_lock)
            {
                _active++;
                _peak = Math.Max(_peak, _active);
                _records.Add(new Record(request.Version, start, end, request.Headers.IfRange?.ToString(), Selected));
            }
            try
            {
                if (TcpUnavailable && request.Version != HttpVersion.Version30)
                    throw new HttpRequestException(HttpRequestError.ConnectionError, "No TCP listener");
                if (Http1Only && request.Version.Major > 1 && request.VersionPolicy == HttpVersionPolicy.RequestVersionExact)
                    throw new HttpRequestException(HttpRequestError.VersionNegotiationError, "HTTP/1.1 only");
                if (request.Version == HttpVersion.Version30)
                {
                    if (NoHttp3)
                        throw new HttpRequestException(HttpRequestError.VersionNegotiationError, "H3 unavailable");
                    if (BlockHttp3)
                    {
                        OnBlocked?.Invoke();
                        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    }
                }
                if (end > 0 && request.Version.Major != Winner && request.VersionPolicy == HttpVersionPolicy.RequestVersionExact)
                    await Task.Delay(250, ct);
                var bytes = Payload.AsSpan((int)start, (int)(end - start + 1)).ToArray();
                HttpContent content = new ByteArrayContent(bytes);
                if (Selected && request.Version == HttpVersion.Version30 && FailHttp3Body && Interlocked.Exchange(ref _failed, 1) == 0)
                {
                    FailedStart = start;
                    content = new StreamContent(new BrokenStream(bytes));
                    content.Headers.ContentLength = bytes.Length;
                }
                content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, Payload.Length);
                return new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Version = Http1Only ? HttpVersion.Version11 : request.Version,
                    RequestMessage = request,
                    Headers = { ETag = new EntityTagHeaderValue("\"entity\"") },
                    Content = content
                };
            }
            finally { lock (_lock) _active--; }
        }
        // Keep the transport available for the resume test's second downloader.
        protected override void Dispose(bool disposing) { }
    }

    private sealed class BrokenStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        private bool _read;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_read) throw new HttpIOException(HttpRequestError.HttpProtocolError, "QUIC stream reset");
            _read = true;
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, 8192)], ct);
        }
    }
}
