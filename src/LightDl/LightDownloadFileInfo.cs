namespace LightDl;

/// <summary>
/// Remote file information.
/// </summary>
public sealed class LightDownloadFileInfo
{
    public required string FileName { get; init; }

    public string? FilePath { get; init; }

    public required long Size { get; init; }

    public string? ContentType { get; init; }

    public bool SupportsRange { get; init; }
}
