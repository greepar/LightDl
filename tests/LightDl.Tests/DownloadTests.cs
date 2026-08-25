using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace LightDl.Tests;

public sealed class DownloadTests : IDisposable
{
    private const string Url = "http://origin.test/movie.bin";
    private readonly string _directory = Directory.CreateTempSubdirectory("lightdl-tests-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Path(string name) => System.IO.Path.Combine(_directory, name);

    private static byte[] MakeContent(int length)
    {
        var content = new byte[length];
        Random.Shared.NextBytes(content);
        return content;
    }

    private static LightDownloadConfig BaseConfig(FakeOrigin origin) => origin.Configure(new LightDownloadConfig
    {
        ChunkCount = 4,
        SegmentSize = 1024 * 1024,
        MaxSegmentSize = 1024 * 1024,
        MinSegmentSize = 64 * 1024,
        BufferSize = 16 * 1024,
        ProgressIntervalMs = 100,
        RetryBaseDelay = TimeSpan.FromMilliseconds(1),
        MaxRetryDelay = TimeSpan.FromMilliseconds(20),
        MaxRetry = 5,
        FileConflictPolicy = LightDownloadFileConflictPolicy.Overwrite,
        CheckFreeSpace = false
    });

    // --- Regression: an explicit ChunkCount must not be raised by MinChunkCount ------------------

    [Fact]
    public async Task ChunkCount_Of_One_Opens_A_Single_Connection()
    {
        var origin = new FakeOrigin(MakeContent(4 * 1024 * 1024));
        var config = BaseConfig(origin);
        config.ChunkCount = 1;
        config.MinChunkCount = 4; // the documented default, which used to win

        using var downloader = new LightDownloader(config);
        await downloader.DownloadAsync(LightDownloadRequest.ToFile(Url, Path("out.bin")));

        Assert.Equal(1, origin.PeakConcurrentRequests);
    }

    [Fact]
    public async Task ChunkCount_Of_Four_Opens_Four_Connections()
    {
        var origin = new FakeOrigin(MakeContent(4 * 1024 * 1024)) { BodyDelay = TimeSpan.FromMilliseconds(5) };
        using var downloader = new LightDownloader(BaseConfig(origin));
        await downloader.DownloadAsync(LightDownloadRequest.ToFile(Url, Path("out.bin")));

        Assert.Equal(4, origin.PeakConcurrentRequests);
    }

    // --- Regression: a fatal segment failure must stop every other worker at once ----------------

    [Fact]
    public async Task Fatal_Segment_Failure_Stops_The_Other_Workers_Immediately()
    {
        var origin = new FakeOrigin(MakeContent(4 * 1024 * 1024))
        {
            // Every other segment would take ~16 * 2s to stream to completion.
            BodyDelay = TimeSpan.FromSeconds(2),
            FailRangeAt = (1024 * 1024, HttpStatusCode.NotFound)
        };

        using var downloader = new LightDownloader(BaseConfig(origin));
        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<LightDownloadException>(() =>
            downloader.DownloadAsync(LightDownloadRequest.ToFile(Url, Path("out.bin"))));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(8),
            $"the download kept running for {stopwatch.Elapsed.TotalSeconds:F1}s after a fatal failure");
    }

    // --- Regression: internal signalling exceptions must not escape ------------------------------

    [Fact]
    public async Task Failure_Surfaces_A_Public_Exception_Type()
    {
        var origin = new FakeOrigin(MakeContent(64 * 1024))
        {
            FailFirstRequests = (100, HttpStatusCode.ServiceUnavailable, null)
        };
        var config = BaseConfig(origin);
        config.MaxRetry = 1;

        using var downloader = new LightDownloader(config);
        var error = await Assert.ThrowsAsync<LightDownloadException>(() =>
            downloader.DownloadAsync(LightDownloadRequest.ToFile(Url, Path("out.bin"))));

        Assert.Contains("503", error.Message);
        Assert.True(error.GetType().IsPublic);
    }

    // --- Regression: a failed download must never leave a file at the destination name -----------

    [Fact]
    public async Task NonResumable_Failure_Leaves_Nothing_Behind()
    {
        var origin = new FakeOrigin(MakeContent(4 * 1024 * 1024))
        {
            SupportsRange = false,
            TruncateCount = -1,
            TruncateBodyRatio = 0.5
        };
        var config = BaseConfig(origin);
        config.EnableResume = true;
        config.MaxRetry = 1;

        using var downloader = new LightDownloader(config);
        await Assert.ThrowsAsync<LightDownloadException>(() =>
            downloader.DownloadAsync(LightDownloadRequest.ToFile(Url, Path("movie.bin"))));

        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task NonResumable_Download_Retries_A_Dropped_Connection()
    {
        var origin = new FakeOrigin(MakeContent(1024 * 1024))
        {
            SupportsRange = false,
            TruncateCount = 2, // two dropped connections, then a clean one
            TruncateBodyRatio = 0.5
        };

        using var downloader = new LightDownloader(BaseConfig(origin));
        var result = await downloader.DownloadAsync(LightDownloadRequest.ToFile(Url, Path("movie.bin")));

        Assert.Equal(origin.Content, await File.ReadAllBytesAsync(result.FilePath));
    }

    // --- Regression: resume must revalidate the remote content ----------------------------------

    [Fact]
    public async Task Resume_Restarts_When_The_Remote_File_Changed()
    {
        var content = MakeContent(1024 * 1024);
        var destination = Path("movie.bin");
        WritePartialDownload(destination, content.Length, completedTo: 512 * 1024 - 1, eTag: "\"v1\"");

        var origin = new FakeOrigin(content) { ETag = "\"v2\"" };
        using var downloader = new LightDownloader(BaseConfig(origin));
        var result = await downloader.DownloadAsync(LightDownloadRequest.ToFile(Url, destination));

        // The stale half must not survive into the finished file.
        Assert.Equal(content, await File.ReadAllBytesAsync(result.FilePath));
        Assert.Contains(origin.Requests, r => r.From == 0 && r.To > 0);
    }

    [Fact]
    public async Task Resume_Reuses_Partial_Data_When_The_Validator_Still_Matches()
    {
        var content = MakeContent(1024 * 1024);
        var destination = Path("movie.bin");
        WritePartialDownload(destination, content.Length, completedTo: 512 * 1024 - 1, eTag: "\"v1\"",
            data: content);

        var origin = new FakeOrigin(content) { ETag = "\"v1\"" };
        using var downloader = new LightDownloader(BaseConfig(origin));
        var result = await downloader.DownloadAsync(LightDownloadRequest.ToFile(Url, destination));

        Assert.Equal(content, await File.ReadAllBytesAsync(result.FilePath));
        // Only the missing half is fetched; the probe (0-0) does not count as a range download.
        Assert.DoesNotContain(origin.Requests, r => r.From == 0 && r.To > 0);
    }

    private void WritePartialDownload(string destination, int totalLength, long completedTo, string eTag,
        byte[]? data = null, bool legacyMetadataName = false)
    {
        var temp = destination + ".lightdl";
        var buffer = new byte[totalLength];
        if (data is not null)
            Array.Copy(data, buffer, completedTo + 1);

        File.WriteAllBytes(temp, buffer);
        var metadata = JsonSerializer.Serialize(new
        {
            Url,
            TotalLength = (long)totalLength,
            ETag = eTag,
            LastModified = (string?)null,
            CompletedRanges = new[] { new { Start = 0L, End = completedTo } }
        });
        File.WriteAllText(MetadataPath(destination, legacyMetadataName), metadata);
    }

    private static string MetadataPath(string destination, bool legacy)
    {
        var visible = destination + ".lightdl.meta";
        if (legacy || OperatingSystem.IsWindows())
            return visible;

        return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(visible)!,
            "." + System.IO.Path.GetFileName(visible));
    }

    // --- Resume metadata stays out of the way -----------------------------------------------------

    [Fact]
    public async Task Interrupted_Download_Leaves_Hidden_Resume_Metadata()
    {
        var origin = new FakeOrigin(MakeContent(4 * 1024 * 1024)) { BodyDelay = TimeSpan.FromMilliseconds(200) };
        var destination = Path("movie.bin");

        using var downloader = new LightDownloader(BaseConfig(origin));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            downloader.DownloadAsync(LightDownloadRequest.ToFile(Url, destination), cts.Token));

        var metadata = Directory.GetFiles(_directory)
            .Select(System.IO.Path.GetFileName)
            .Single(name => name!.EndsWith(".lightdl.meta"));

        if (OperatingSystem.IsWindows())
            Assert.True(File.GetAttributes(MetadataPath(destination, legacy: false)).HasFlag(FileAttributes.Hidden));
        else
            Assert.StartsWith(".", metadata);
    }

    [Fact]
    public async Task Opting_Out_Keeps_The_Visible_Metadata_Name()
    {
        var origin = new FakeOrigin(MakeContent(4 * 1024 * 1024)) { BodyDelay = TimeSpan.FromMilliseconds(200) };
        var config = BaseConfig(origin);
        config.HideMetadataFile = false;

        using var downloader = new LightDownloader(config);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            downloader.DownloadAsync(LightDownloadRequest.ToFile(Url, Path("movie.bin")), cts.Token));

        Assert.Contains(Directory.GetFiles(_directory).Select(System.IO.Path.GetFileName),
            name => name == "movie.bin.lightdl.meta");
    }

    [Fact]
    public async Task Metadata_Written_By_An_Older_Version_Is_Adopted_Not_Restarted()
    {
        var content = MakeContent(1024 * 1024);
        var destination = Path("movie.bin");
        // 0.3.0 and earlier wrote the metadata under the visible name.
        WritePartialDownload(destination, content.Length, completedTo: 512 * 1024 - 1, eTag: "\"v1\"",
            data: content, legacyMetadataName: true);

        var origin = new FakeOrigin(content) { ETag = "\"v1\"" };
        using var downloader = new LightDownloader(BaseConfig(origin));
        var result = await downloader.DownloadAsync(LightDownloadRequest.ToFile(Url, destination));

        Assert.Equal(content, await File.ReadAllBytesAsync(result.FilePath));
        // The already-downloaded half was reused rather than fetched again...
        Assert.DoesNotContain(origin.Requests, r => r.From == 0 && r.To > 0);
        // ...and the old file was not left orphaned.
        Assert.Equal(["movie.bin"], Directory.GetFiles(_directory).Select(System.IO.Path.GetFileName));
    }

    // --- Regression: client errors must not be retried, server errors must be -------------------

    [Fact]
    public async Task Client_Error_Is_Not_Retried()
    {
        var origin = new FakeOrigin(MakeContent(64 * 1024))
        {
            FailFirstRequests = (100, HttpStatusCode.Forbidden, null)
        };
        var config = BaseConfig(origin);
        config.MaxRetry = 20;

        using var downloader = new LightDownloader(config);
        await Assert.ThrowsAsync<LightDownloadException>(() =>
            downloader.DownloadAsync(LightDownloadRequest.ToFile(Url, Path("out.bin"))));

        // One probe attempt only; a 403 will not become a 200 by asking 20 more times.
        Assert.True(origin.Requests.Count <= 2, $"a 403 was retried {origin.Requests.Count} times");
    }

    [Fact]
    public async Task Server_Error_Is_Retried_And_Honours_Retry_After()
    {
        var origin = new FakeOrigin(MakeContent(256 * 1024))
        {
            FailFirstRequests = (2, HttpStatusCode.ServiceUnavailable, TimeSpan.FromMilliseconds(50))
        };
        var config = BaseConfig(origin);
        config.MaxRetry = 10;

        using var downloader = new LightDownloader(config);
        var result = await downloader.DownloadAsync(LightDownloadRequest.ToFile(Url, Path("out.bin")));

        Assert.Equal(origin.Content, await File.ReadAllBytesAsync(result.FilePath));
    }

    // --- Regression: a truncated range response must be detected, not silently accepted ----------

    [Fact]
    public async Task Truncated_Range_Response_Is_Requeued_And_The_File_Is_Complete()
    {
        var origin = new FakeOrigin(MakeContent(2 * 1024 * 1024))
        {
            TruncateCount = 3,
            TruncateBodyRatio = 0.4
        };

        using var downloader = new LightDownloader(BaseConfig(origin));
        var result = await downloader.DownloadAsync(LightDownloadRequest.ToFile(Url, Path("out.bin")));

        Assert.Equal(origin.Content, await File.ReadAllBytesAsync(result.FilePath));
    }

    // --- Regression: progress must reach 100% and must survive a throwing callback ---------------

    [Fact]
    public async Task Progress_Reaches_One_Hundred_Percent()
    {
        var origin = new FakeOrigin(MakeContent(512 * 1024));
        var reported = new List<LightDownloadProgress>();

        using var downloader = new LightDownloader(BaseConfig(origin));
        var request = LightDownloadRequest.ToFile(Url, Path("out.bin"))
            .OnProgress(p =>
            {
                lock (reported) reported.Add(p);
            });
        await downloader.DownloadAsync(request);

        Assert.NotEmpty(reported);
        Assert.Equal(100d, reported[^1].ProgressPercentage, 5);
        Assert.Equal(origin.Content.Length, reported[^1].DownloadedBytes);
    }

    [Fact]
    public async Task A_Throwing_Progress_Callback_Does_Not_Fail_The_Download()
    {
        var origin = new FakeOrigin(MakeContent(512 * 1024));
        using var downloader = new LightDownloader(BaseConfig(origin));
        var request = LightDownloadRequest.ToFile(Url, Path("out.bin"))
            .OnProgress(_ => throw new InvalidOperationException("callback blew up"))
            .OnFileInfo(_ => throw new InvalidOperationException("callback blew up"));

        var result = await downloader.DownloadAsync(request);
        Assert.Equal(origin.Content, await File.ReadAllBytesAsync(result.FilePath));
    }

    // --- New: checksum verification --------------------------------------------------------------

    [Fact]
    public async Task Checksum_Mismatch_Fails_And_Removes_The_File()
    {
        var origin = new FakeOrigin(MakeContent(256 * 1024));
        var config = BaseConfig(origin);
        config.ChecksumAlgorithm = LightDownloadChecksumAlgorithm.Sha256;
        config.ExpectedChecksum = new string('a', 64);

        using var downloader = new LightDownloader(config);
        var error = await Assert.ThrowsAsync<LightDownloadException>(() =>
            downloader.DownloadAsync(LightDownloadRequest.ToFile(Url, Path("out.bin"))));

        Assert.Contains("checksum mismatch", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task Matching_Checksum_Passes()
    {
        var origin = new FakeOrigin(MakeContent(256 * 1024));
        var config = BaseConfig(origin);
        config.ChecksumAlgorithm = LightDownloadChecksumAlgorithm.Sha256;
        config.ExpectedChecksum = Convert.ToHexString(SHA256.HashData(origin.Content));

        using var downloader = new LightDownloader(config);
        var result = await downloader.DownloadAsync(LightDownloadRequest.ToFile(Url, Path("out.bin")));

        Assert.Equal(origin.Content, await File.ReadAllBytesAsync(result.FilePath));
    }

    // --- New: servers that do not declare a length ----------------------------------------------

    [Fact]
    public async Task Unknown_Content_Length_Downloads_As_A_Single_Stream()
    {
        var origin = new FakeOrigin(MakeContent(512 * 1024))
        {
            SupportsRange = false,
            OmitContentLength = true
        };

        using var downloader = new LightDownloader(BaseConfig(origin));
        var result = await downloader.DownloadAsync(LightDownloadRequest.ToFile(Url, Path("out.bin")));

        Assert.Equal(origin.Content.Length, result.Size);
        Assert.Equal(origin.Content, await File.ReadAllBytesAsync(result.FilePath));
    }

    // --- New: range requests must never ask for a content coding ---------------------------------

    [Fact]
    public async Task Requests_Ask_For_Identity_Encoding_And_Send_If_Range()
    {
        var origin = new FakeOrigin(MakeContent(512 * 1024)) { ETag = "\"v1\"" };
        using var downloader = new LightDownloader(BaseConfig(origin));
        await downloader.DownloadAsync(LightDownloadRequest.ToFile(Url, Path("out.bin")));

        Assert.All(origin.Requests, r => Assert.Equal("identity", r.AcceptEncoding));
        Assert.Contains(origin.Requests, r => r.To > 0 && r.IfRange == "\"v1\"");
    }

    // --- New: a misconfigured handler is rejected up front --------------------------------------

    [Fact]
    public void A_Handler_With_Automatic_Redirects_Is_Rejected()
    {
        var config = new LightDownloadConfig
        {
            HttpMessageHandlerFactory = () => new SocketsHttpHandler { AllowAutoRedirect = true }
        };

        var error = Assert.Throws<ArgumentException>(() => new LightDownloader(config));
        Assert.Contains("AllowAutoRedirect", error.Message);
    }

    [Fact]
    public void A_Handler_With_Automatic_Decompression_Is_Rejected()
    {
        var config = new LightDownloadConfig
        {
            HttpMessageHandlerFactory = () => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.GZip
            }
        };

        var error = Assert.Throws<ArgumentException>(() => new LightDownloader(config));
        Assert.Contains("AutomaticDecompression", error.Message);
    }
}
