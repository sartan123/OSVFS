using System.Text;

namespace S3Files.Windows.S3;

internal static class S3Util
{
    /// <summary>Streams at or above this size are routed through TransferUtility's multipart
    /// path. Picked to be well above the S3 5 MiB minimum part size so the multipart overhead
    /// is worth paying.</summary>
    public const long MultipartThresholdBytes = 8L * 1024 * 1024;

    /// <summary>Per-part size for multipart uploads. Must be ≥ 5 MiB to satisfy the S3
    /// minimum; the last part is allowed to be smaller.</summary>
    public const long MultipartPartSizeBytes = 5L * 1024 * 1024;

    /// <summary>
    /// Length of the ProjFS contentId we derive from an ETag. ProjFS allows up to 128 bytes;
    /// 16 bytes is enough to make placeholders comparable across runs without bloating each
    /// placeholder's metadata.
    /// </summary>
    public const int ContentIdLength = 16;

    public static string ToS3Key(string relativePath) =>
        string.IsNullOrEmpty(relativePath) ? string.Empty : relativePath.Replace('\\', '/');

    public static string ToRelativePath(string s3Key) =>
        s3Key.Replace('/', '\\');

    public static string NormalizePrefix(string relativeDirectory)
    {
        var prefix = ToS3Key(relativeDirectory);
        return prefix.Length > 0 && !prefix.EndsWith('/') ? prefix + '/' : prefix;
    }

    /// <summary>
    /// Derives a stable, fixed-size ProjFS contentId from an S3 ETag. Surrounding quotes on
    /// the ETag (S3's wire format) are stripped before hashing into the buffer.
    /// </summary>
    public static byte[] BuildContentId(string? etag)
    {
        var result = new byte[ContentIdLength];
        if (string.IsNullOrEmpty(etag)) return result;

        var trimmed = etag.AsSpan().Trim('"');
        var byteCount = Encoding.UTF8.GetByteCount(trimmed);
        Span<byte> bytes = byteCount <= 256 ? stackalloc byte[byteCount] : new byte[byteCount];
        Encoding.UTF8.GetBytes(trimmed, bytes);
        bytes[..Math.Min(bytes.Length, result.Length)].CopyTo(result);
        return result;
    }

    /// <summary>Returns the trailing-slash-terminated linked prefix, or the empty string when
    /// no prefix is configured. Always lives in S3 key form (forward-slash separated).</summary>
    public static string NormalizeKeyPrefix(string? prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return string.Empty;
        var trimmed = prefix.Replace('\\', '/').Trim('/');
        return trimmed.Length == 0 ? string.Empty : trimmed + "/";
    }

    /// <summary>Maps a virt-root-relative key (or empty) to the full S3 key applied against
    /// the bucket. The empty input maps to the prefix itself (typically used as a list root).</summary>
    public static string FullKey(string keyPrefix, string relativeKey) =>
        relativeKey.Length == 0 ? keyPrefix : keyPrefix + relativeKey;

    /// <summary>Maps a virt-root-relative directory to the full prefix used for List operations.
    /// The empty directory yields the linked prefix itself, so an empty bucket and an empty
    /// linked prefix both list the same way.</summary>
    public static string FullPrefix(string keyPrefix, string relativeDirectory) =>
        keyPrefix + NormalizePrefix(relativeDirectory);

    /// <summary>Strips the linked prefix back off a full S3 key. Defensive: keys that don't
    /// start with the prefix are returned unchanged so a misrouted result is surfaced rather
    /// than silently mapped to a bogus relative path.</summary>
    public static string StripPrefix(string keyPrefix, string fullKey)
    {
        if (keyPrefix.Length == 0) return fullKey;
        return fullKey.StartsWith(keyPrefix, StringComparison.Ordinal)
            ? fullKey[keyPrefix.Length..]
            : fullKey;
    }
}
