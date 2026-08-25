namespace LightDl;

/// <summary>
/// Hash algorithm used to verify a completed download.
/// </summary>
public enum LightDownloadChecksumAlgorithm
{
    /// <summary>No verification.</summary>
    None = 0,
    Md5,
    Sha1,
    Sha256,
    Sha512
}
