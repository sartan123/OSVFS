using Microsoft.Extensions.Logging.Abstractions;
using OSVFS.ObjectStore;
using OSVFS.ProjFs;
using Xunit;

namespace OSVFS.UnitTests.ProjFs;

/// <summary>
/// Exercises the safety-policy decision table that
/// <see cref="BucketVersioningGuard"/> applies before ProjFS is touched. The
/// guard is the only place where the operator-facing remediation message is
/// emitted, so the tests assert against its exact wording.
/// </summary>
public class BucketVersioningGuardTests
{
    private const string Bucket = "my-bucket";

    /// <summary>
    /// Stand-in for the backend-supplied "how to enable versioning" snippet.
    /// Mirrors the real S3 backend's wording so the message-shape assertions
    /// stay close to production.
    /// </summary>
    private const string S3Instructions =
        "  aws s3api put-bucket-versioning --bucket my-bucket --versioning-configuration Status=Enabled";

    [Fact]
    public void Validate_returns_silently_when_versioning_enabled()
    {
        // Should not throw or warn for the green path.
        BucketVersioningGuard.Validate(
            BucketVersioningStatus.Enabled,
            Bucket,
            S3Instructions,
            allowUnversioned: false,
            NullLogger.Instance);
    }

    [Fact]
    public void Validate_throws_when_versioning_not_enabled_and_no_opt_out()
    {
        var ex = Assert.Throws<BucketVersioningNotEnabledException>(() =>
            BucketVersioningGuard.Validate(
                BucketVersioningStatus.NotEnabled,
                Bucket,
                S3Instructions,
                allowUnversioned: false,
                NullLogger.Instance));

        Assert.Equal(Bucket, ex.Bucket);
        Assert.Equal(BucketVersioningStatus.NotEnabled, ex.Status);
    }

    [Fact]
    public void Validate_does_not_throw_when_allow_unversioned_is_true()
    {
        // The guard should not throw; the host is responsible for the periodic warning.
        BucketVersioningGuard.Validate(
            BucketVersioningStatus.NotEnabled,
            Bucket,
            S3Instructions,
            allowUnversioned: true,
            NullLogger.Instance);
    }

    [Fact]
    public void Exception_message_contains_bucket_name_backend_instructions_and_escape_hatch()
    {
        var ex = new BucketVersioningNotEnabledException(
            Bucket, BucketVersioningStatus.NotEnabled, S3Instructions);

        Assert.Contains(Bucket, ex.Message, StringComparison.Ordinal);
        // The backend supplies the actual fix command — the exception just splices it in.
        Assert.Contains(S3Instructions, ex.Message, StringComparison.Ordinal);
        Assert.Contains("--allow-unversioned", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Exception_message_uses_backend_supplied_instructions_verbatim()
    {
        // A different backend (e.g. Azure Blob) returns its own remediation copy;
        // the exception must drop it in unmodified rather than hard-coding AWS wording.
        const string AzureLikeInstructions =
            "  az storage account blob-service-properties update --enable-versioning true ...";
        var ex = new BucketVersioningNotEnabledException(
            Bucket, BucketVersioningStatus.NotEnabled, AzureLikeInstructions);

        Assert.Contains(AzureLikeInstructions, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("aws s3api", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Exception_message_reports_observed_status()
    {
        var ex = new BucketVersioningNotEnabledException(
            Bucket, BucketVersioningStatus.NotEnabled, S3Instructions);

        Assert.Contains("NotEnabled", ex.Message, StringComparison.Ordinal);
    }
}
