using OSVFS.ObjectStore;

namespace OSVFS;

/// <summary>
/// Validates the multipart-upload knobs (<c>multipart-threshold</c> /
/// <c>multipart-part-size</c> in <c>osvfs.toml</c>) against the active
/// provider's bounds, supplied through <see cref="MultipartCapabilities"/>.
/// Returns a human-readable error string on violation, or null when the
/// settings are acceptable. Both inputs may be null, meaning "use the backend
/// default".
/// </summary>
internal static class MultipartSettingsValidator
{
    /// <summary>
    /// Returns null when the supplied threshold and part size satisfy the
    /// provider's <paramref name="capabilities"/>, or a description of the
    /// first violation. The threshold must be positive; the part size must
    /// fall within
    /// <see cref="MultipartCapabilities.MinPartSizeBytes"/>..<see cref="MultipartCapabilities.MaxPartSizeBytes"/>.
    /// </summary>
    public static string? Validate(
        long? thresholdBytes, long? partSizeBytes, MultipartCapabilities capabilities)
    {
        if (thresholdBytes is <= 0)
        {
            return $"'multipart-threshold' must be positive (got {thresholdBytes}).";
        }
        if (partSizeBytes is { } size)
        {
            if (size < capabilities.MinPartSizeBytes)
            {
                return $"'multipart-part-size' must be at least {FormatBytes(capabilities.MinPartSizeBytes)} (got {size}).";
            }
            if (size > capabilities.MaxPartSizeBytes)
            {
                return $"'multipart-part-size' must be at most {FormatBytes(capabilities.MaxPartSizeBytes)} (got {size}).";
            }
        }
        return null;
    }

    /// <summary>
    /// Renders a byte count as "N GiB" / "N MiB" / "N KiB" when <paramref name="bytes"/>
    /// is an exact multiple of that unit, falling back to "N B" otherwise.
    /// Used so the operator-facing error messages keep the original
    /// "5 MiB" / "5 GiB" wording, even though the limit values now come from
    /// the active backend.
    /// </summary>
    private static string FormatBytes(long bytes)
    {
        const long Gib = 1024L * 1024 * 1024;
        const long Mib = 1024L * 1024;
        const long Kib = 1024L;
        if (bytes >= Gib && bytes % Gib == 0) return $"{bytes / Gib} GiB";
        if (bytes >= Mib && bytes % Mib == 0) return $"{bytes / Mib} MiB";
        if (bytes >= Kib && bytes % Kib == 0) return $"{bytes / Kib} KiB";
        return $"{bytes} B";
    }
}
