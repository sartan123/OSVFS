using Amazon.S3;
using Amazon.S3.Model;
using System.Net;

namespace OSVFS.Diagnostics.Checks;

/// <summary>
/// Issues a single S3 <c>HeadBucket</c> against the configured bucket. A 200
/// proves the bucket exists, the request was correctly signed, and the calling
/// principal can reach it. 403 / 404 / network errors all surface as
/// <see cref="DoctorCheckStatus.Fail"/> with a message identifying which side
/// of the wire is at fault.
/// </summary>
internal sealed class BucketAccessCheck : IDoctorCheck
{
    private readonly IAmazonS3 client;
    private readonly string bucket;

    /// <inheritdoc/>
    public string Name => "Bucket access (HeadBucket)";

    /// <summary>
    /// Constructs the check bound to <paramref name="bucket"/> and the
    /// (already-configured) <paramref name="client"/>. Ownership of the client
    /// stays with the caller so several checks can share one instance.
    /// </summary>
    public BucketAccessCheck(IAmazonS3 client, string bucket)
    {
        this.client = client;
        this.bucket = bucket;
    }

    /// <inheritdoc/>
    public async Task<DoctorResult> RunAsync(CancellationToken ct)
    {
        try
        {
            var resp = await client.GetBucketLocationAsync(
                new GetBucketLocationRequest { BucketName = bucket }, ct).ConfigureAwait(false);
            // GetBucketLocation answers as well as HeadBucket for the
            // "exists + reachable + signed" question and additionally tells us
            // the resolved region — the most common cause of "I can't see my
            // bucket" is using the wrong --region for it.
            var loc = string.IsNullOrEmpty(resp.Location?.Value) ? "us-east-1" : resp.Location.Value;
            return new DoctorResult(
                Name,
                DoctorCheckStatus.Pass,
                $"Bucket '{bucket}' is reachable (region {loc}).");
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new DoctorResult(
                Name,
                DoctorCheckStatus.Fail,
                $"Bucket '{bucket}' was not found. Check the spelling and that the configured region matches.");
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
        {
            return new DoctorResult(
                Name,
                DoctorCheckStatus.Fail,
                $"Access denied to bucket '{bucket}'. The principal lacks s3:ListBucket / s3:GetBucketLocation. ({ex.ErrorCode})");
        }
        catch (AmazonS3Exception ex)
        {
            return new DoctorResult(
                Name,
                DoctorCheckStatus.Fail,
                $"S3 error contacting bucket '{bucket}': {ex.StatusCode} {ex.ErrorCode} — {ex.Message}",
                ex.ToString());
        }
        catch (HttpRequestException ex)
        {
            return new DoctorResult(
                Name,
                DoctorCheckStatus.Fail,
                $"Network error reaching the S3 endpoint: {ex.Message}",
                ex.ToString());
        }
    }
}
