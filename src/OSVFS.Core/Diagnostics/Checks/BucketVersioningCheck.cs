using Amazon.S3;
using Amazon.S3.Model;

namespace OSVFS.Diagnostics.Checks;

/// <summary>
/// Reads the bucket's versioning configuration. Versioning is a hard runtime
/// requirement for OSVFS (the conflict-resolution path moves displaced local
/// copies into <c>.osvfs-lost+found</c> and relies on S3 keeping the prior
/// object version). Anything other than the explicit <c>Enabled</c> state is
/// reported as a failure with the exact remediation command.
/// </summary>
internal sealed class BucketVersioningCheck : IDoctorCheck
{
    private readonly IAmazonS3 client;
    private readonly string bucket;

    /// <inheritdoc/>
    public string Name => "Bucket versioning";

    /// <summary>
    /// Constructs the check bound to <paramref name="bucket"/>.
    /// </summary>
    public BucketVersioningCheck(IAmazonS3 client, string bucket)
    {
        this.client = client;
        this.bucket = bucket;
    }

    /// <inheritdoc/>
    public async Task<DoctorResult> RunAsync(CancellationToken ct)
    {
        GetBucketVersioningResponse resp;
        try
        {
            resp = await client.GetBucketVersioningAsync(
                new GetBucketVersioningRequest { BucketName = bucket }, ct).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex)
        {
            return new DoctorResult(
                Name,
                DoctorCheckStatus.Fail,
                $"Could not read versioning status for '{bucket}': {ex.ErrorCode} — {ex.Message}",
                ex.ToString());
        }

        var status = resp.VersioningConfig?.Status?.Value;
        if (status == "Enabled")
        {
            return new DoctorResult(
                Name,
                DoctorCheckStatus.Pass,
                $"Versioning is enabled on '{bucket}'.");
        }

        // The SDK fills VersioningConfig.Status with the literal "Off" when the
        // bucket has never had a PutBucketVersioning call applied; collapse that
        // and an empty status into the same human-readable phrase so the operator
        // doesn't have to know the SDK quirk.
        var human = string.IsNullOrEmpty(status) || status == "Off" ? "never configured" : status;
        return new DoctorResult(
            Name,
            DoctorCheckStatus.Fail,
            $"Versioning is {human} on '{bucket}'. Run: aws s3api put-bucket-versioning --bucket {bucket} --versioning-configuration Status=Enabled");
    }
}
