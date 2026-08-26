using System.Reflection;
using Xunit;

namespace LightDl.Tests;

public sealed class ConfigTests
{
    private static LightDownloadConfig Normalize(LightDownloadConfig config)
    {
        var clone = (LightDownloadConfig)typeof(LightDownloadConfig)
            .GetMethod("Clone", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(config, [])!;

        typeof(LightDownloader).GetMethod("NormalizeConfig", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [clone]);
        return clone;
    }

    [Fact]
    public void MinChunkCount_Never_Raises_An_Explicit_ChunkCount()
    {
        var config = Normalize(new LightDownloadConfig { ChunkCount = 1, MinChunkCount = 8 });

        Assert.Equal(1, config.ChunkCount);
        Assert.Equal(1, config.MinChunkCount);
    }

    [Fact]
    public void Inverted_Segment_Bounds_Are_Normalised_Instead_Of_Throwing()
    {
        var config = Normalize(new LightDownloadConfig
        {
            MinSegmentSize = 32L * 1024 * 1024,
            MaxSegmentSize = 16L * 1024 * 1024,
            SegmentSize = 8L * 1024 * 1024
        });

        Assert.True(config.MinSegmentSize <= config.SegmentSize);
        Assert.True(config.SegmentSize <= config.MaxSegmentSize);
    }

    [Fact]
    public void Inverted_Chunk_Bounds_Are_Normalised()
    {
        var config = Normalize(new LightDownloadConfig { ChunkCount = 32, MaxChunkCount = 8 });

        Assert.Equal(32, config.ChunkCount);
        Assert.True(config.MaxChunkCount >= config.ChunkCount);
    }

    [Fact]
    public void An_Inverted_Retry_Window_Is_Normalised()
    {
        var config = Normalize(new LightDownloadConfig
        {
            RetryBaseDelay = TimeSpan.FromSeconds(5),
            MaxRetryDelay = TimeSpan.FromSeconds(1),
            NoDataTimeout = TimeSpan.Zero
        });

        Assert.True(config.MaxRetryDelay >= config.RetryBaseDelay);
        Assert.True(config.NoDataTimeout > TimeSpan.Zero);
    }

    private static long SegmentSize(LightDownloadConfig config, long totalLength, int concurrency)
    {
        var downloader = new LightDownloader(config);
        try
        {
            return (long)typeof(LightDownloader)
                .GetMethod("CalculateStableSegmentSize", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(downloader, [totalLength, concurrency])!;
        }
        finally
        {
            downloader.Dispose();
        }
    }

    [Fact]
    public void A_Large_File_Gets_Segments_Big_Enough_To_Amortise_The_Request()
    {
        // 1.4 GB over 16 workers. 16 MB segments measured 58 MB/s against a per-connection
        // throttled origin where ~48 MB reached 88 MB/s.
        var size = SegmentSize(new LightDownloadConfig(), 1_522_314_091L, 16);

        Assert.InRange(size, 32L * 1024 * 1024, 64L * 1024 * 1024);
    }

    [Fact]
    public void A_Small_File_Still_Divides_Across_Every_Worker()
    {
        var totalLength = 8L * 1024 * 1024;
        var size = SegmentSize(new LightDownloadConfig(), totalLength, 16);

        // Every worker must have something to do rather than one worker taking the whole file.
        Assert.True(totalLength / size >= 16, $"{totalLength / size} segments for 16 workers");
    }

    [Fact]
    public void An_Explicit_SegmentSize_Is_Still_An_Upper_Bound()
    {
        var config = new LightDownloadConfig { SegmentSize = 4L * 1024 * 1024, MaxSegmentSize = 4L * 1024 * 1024 };
        var size = SegmentSize(config, 1_522_314_091L, 16);

        Assert.Equal(4L * 1024 * 1024, size);
    }

    private static TimeSpan StallTimeout(LightDownloadConfig config, int active, int concurrency)
    {
        var downloader = new LightDownloader(config);
        try
        {
            return (TimeSpan)typeof(LightDownloader)
                .GetMethod("GetStallTimeout", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(downloader, [active, concurrency])!;
        }
        finally
        {
            downloader.Dispose();
        }
    }

    [Fact]
    public void A_Busy_Download_Waits_The_Full_NoDataTimeout()
    {
        var config = new LightDownloadConfig { NoDataTimeout = TimeSpan.FromSeconds(20) };

        Assert.Equal(TimeSpan.FromSeconds(20), StallTimeout(config, active: 24, concurrency: 24));
    }

    [Fact]
    public void Idle_Workers_Shorten_The_Wait_On_A_Stalled_Connection()
    {
        var config = new LightDownloadConfig { NoDataTimeout = TimeSpan.FromSeconds(20) };

        // Measured on a bed where the tail stalls: 95.4s -> 67.8s median over 5 runs.
        var idle = StallTimeout(config, active: 2, concurrency: 24);

        Assert.True(idle < TimeSpan.FromSeconds(20), $"idle wait was {idle}");
        Assert.True(idle >= TimeSpan.FromSeconds(3), $"idle wait was {idle}");
    }

    [Fact]
    public void A_Short_NoDataTimeout_Is_Never_Lengthened()
    {
        var config = new LightDownloadConfig { NoDataTimeout = TimeSpan.FromSeconds(1) };

        Assert.Equal(TimeSpan.FromSeconds(1), StallTimeout(config, active: 1, concurrency: 24));
    }
}
