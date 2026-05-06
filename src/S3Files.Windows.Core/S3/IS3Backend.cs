namespace S3Files.Windows.S3;

/// <summary>
/// Result of a successful upload, used to update the watcher's snapshot so the next
/// poll doesn't re-import our own write.
/// </summary>
internal readonly record struct UploadResult(string ETag, string VersionId, long Size, DateTimeOffset LastModified);

/// <summary>
/// Coarse versioning state of the linked bucket; only the "Enabled" case satisfies
/// the startup safety check.
/// </summary>
internal enum BucketVersioningStatus
{
    /// <summary>
    /// Versioning has never been configured, or was suspended after being enabled.
    /// </summary>
    NotEnabled,

    /// <summary>
    /// Versioning is actively protecting the bucket against accidental overwrites.
    /// </summary>
    Enabled,
}

/// <summary>
/// Abstraction over the subset of S3 operations the virtualization layer requires.
/// Path arguments are virt-root-relative; the implementation is responsible for prepending
/// any configured key prefix.
/// </summary>
internal interface IS3Backend
{
    /// <summary>
    /// Enumerates immediate children of <paramref name="relativeDirectory"/> using
    /// the "/" delimiter, yielding both real objects and synthesized directory entries.
    /// </summary>
    IAsyncEnumerable<S3ObjectInfo> ListAsync(string relativeDirectory, CancellationToken ct);

    /// <summary>
    /// Enumerates every object under the linked prefix (no delimiter). Used by the
    /// change watcher to take a full bucket snapshot.
    /// </summary>
    IAsyncEnumerable<S3ObjectInfo> ListAllAsync(CancellationToken ct);

    /// <summary>
    /// Recursively enumerates every object beneath <paramref name="relativeDirectory"/>
    /// (no delimiter), yielding only real objects.
    /// </summary>
    IAsyncEnumerable<S3ObjectInfo> ListRecursiveAsync(string relativeDirectory, CancellationToken ct);

    /// <summary>
    /// Reads the bucket's current versioning status.
    /// </summary>
    Task<BucketVersioningStatus> GetBucketVersioningStatusAsync(CancellationToken ct);

    /// <summary>
    /// Returns metadata for a single object, or a synthesized directory entry if the
    /// path corresponds to a common prefix; null when nothing matches.
    /// </summary>
    Task<S3ObjectInfo?> HeadAsync(string relativePath, CancellationToken ct);

    /// <summary>
    /// Streams a byte range of an object into <paramref name="destination"/>.
    /// </summary>
    Task ReadRangeAsync(string relativePath, long offset, long length, Stream destination, CancellationToken ct);

    /// <summary>
    /// Uploads <paramref name="content"/> as the named object. When
    /// <paramref name="ifMatchETag"/> is supplied the upload uses a single PutObject with the
    /// IfMatch precondition; otherwise TransferUtility decides between simple and multipart.
    /// </summary>
    Task<UploadResult> UploadAsync(string relativePath, Stream content, string? ifMatchETag, CancellationToken ct);

    /// <summary>
    /// Deletes a single object. Missing keys are treated as success.
    /// </summary>
    Task DeleteAsync(string relativePath, CancellationToken ct);

    /// <summary>
    /// Deletes every object beneath the given directory in batches of up to 1000.
    /// </summary>
    Task DeletePrefixAsync(string relativeDirectory, CancellationToken ct);

    /// <summary>
    /// Renames a single object via copy + delete.
    /// </summary>
    Task RenameAsync(string oldRelativePath, string newRelativePath, CancellationToken ct);

    /// <summary>
    /// Renames every object under a directory by copying each to the new prefix and
    /// batch-deleting the originals.
    /// </summary>
    Task RenamePrefixAsync(string oldRelativeDirectory, string newRelativeDirectory, CancellationToken ct);
}
