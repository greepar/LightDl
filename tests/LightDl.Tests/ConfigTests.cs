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
}
