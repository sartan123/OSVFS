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
    /// Limits enforced by Azure Block Blob's <c>StageBlock</c> +
    /// <c>CommitBlockList</c> path: blocks may be 1 byte to 4000 MiB each
    /// (the documented per-block ceiling raised from 100 MiB in 2019), and
    /// a Block Blob can hold up to 50 000 committed blocks. The OSVFS
    /// validator clamps <c>multipart-part-size</c> against these so a config
    /// rejected by the service surfaces the error at startup instead of at
    /// commit time.
    /// </summary>
    public static MultipartCapabilities AzureBlob { get; } = new(
        MinPartSizeBytes: 1L,
        MaxPartSizeBytes: 4000L * 1024 * 1024,
        MaxPartCount: 50_000);

    /// <summary>
    /// Returns the multipart capabilities the validator should apply for
    /// <paramref name="provider"/>. Unknown / not-yet-implemented arms fall
    /// back to <see cref="S3"/>: the conservative S3 bounds are also a safe
    /// approximation for most S3-compatible services.
    /// </summary>
    public static MultipartCapabilities For(ObjectStoreProvider provider) =>
        provider switch
        {
            ObjectStoreProvider.S3 => S3,
            ObjectStoreProvider.AzureBlob => AzureBlob,
            // GCS (resumable 256 KiB-aligned chunks, 5 TiB total) lands here.
            _ => S3,
        };
}
