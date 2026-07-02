namespace LightDl;

/// <summary>
/// Completed download result.
/// </summary>
public sealed class LightDownloadResult
{
    public required string FileName { get; init; }

    public required string FilePath { get; init; }

    public required long Size { get; init; }

    public string? ContentType { get; init; }

    public bool SupportsRange { get; init; }

    public bool Skipped { get; init; }
}
