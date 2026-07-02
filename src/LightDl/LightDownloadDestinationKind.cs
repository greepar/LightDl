namespace LightDl;

/// <summary>
/// Defines how the destination path is interpreted.
/// </summary>
public enum LightDownloadDestinationKind
{
    /// <summary>The destination path is the final file path.</summary>
    File,

    /// <summary>The destination path is a directory and the remote file name is used.</summary>
    Directory
}
