namespace LightDl;

/// <summary>
/// Defines how an existing destination file is handled before a download starts.
/// </summary>
public enum LightDownloadFileConflictPolicy
{
    /// <summary>Replace the existing file.</summary>
    Overwrite,

    /// <summary>Fail the download if the destination file already exists.</summary>
    Fail,

    /// <summary>Return immediately without downloading if the destination file already exists.</summary>
    Skip,

    /// <summary>Choose a unique file name if the destination file already exists.</summary>
    Rename
}
