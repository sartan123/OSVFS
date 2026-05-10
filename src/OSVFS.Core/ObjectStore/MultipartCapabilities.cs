namespace OSVFS.ObjectStore;

/// <summary>
/// Provider-specific bounds on the multipart / chunked upload path. Lifted out
/// of <c>MultipartSettingsValidator</c> so the same validator can run against
/// S3, Azure Blob (Block Blob commit), and GCS (resumable upload) without
/// hard-coding any single provider's numbers.
/// </summary>
/// <param name="MinPartSizeBytes">
/// Smallest per-part size accepted by the provider. Parts smaller than this
/// fail the upload completion call (the last part is typically exempt).
/// </param>
/// <param name="MaxPartSizeBytes">
/// Largest per-part size accepted by the provider.
/// </param>
/// <param name="MaxPartCount">
/// Hard cap on the number of parts a single multipart / chunked upload may
/// have. Combined with <see cref="MaxPartSizeBytes"/> this caps the largest
/// single-object size achievable with the configured part size.
/// </param>
internal sealed record MultipartCapabilities(
    long MinPartSizeBytes,
    long MaxPartSizeBytes,
    int MaxPartCount)
{
    /// <summary>
    /// Limits enforced by Amazon S3 (and most S3-compatible providers): 5 MiB
    /// minimum part, 5 GiB maximum part, 10 000 parts per upload.
    /// </summary>
    public static MultipartCapabilities S3 { get; } = new(
        MinPartSizeBytes: 5L * 1024 * 1024,
        MaxPartSizeBytes: 5L * 1024 * 1024 * 1024,
        MaxPartCount: 10_000);

    /// <summary>
    /// Returns the multipart capabilities the validator should apply for
    /// <paramref name="provider"/>. Falls back to <see cref="S3"/> for
    /// providers whose backend has not landed yet, since the conservative S3
    /// bounds are also a safe approximation for most S3-compatible services.
    /// </summary>
    public static MultipartCapabilities For(ObjectStoreProvider provider) =>
        provider switch
        {
            ObjectStoreProvider.S3 => S3,
            // Future arms — Azure Blob (Block Blob 4 GiB / 50 000 blocks) and
            // GCS (resumable 256 KiB-aligned chunks, 5 TiB total) — land here.
            _ => S3,
        };
}
