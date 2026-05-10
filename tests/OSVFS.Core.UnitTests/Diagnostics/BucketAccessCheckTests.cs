using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using OSVFS.Diagnostics;
using OSVFS.Diagnostics.Checks;
using System.Net;
using Xunit;

namespace OSVFS.Core.UnitTests.Diagnostics;

/// <summary>
/// Drives <see cref="BucketAccessCheck"/> against a fake <see cref="AmazonS3Client"/>
/// so the four reachable / not-found / access-denied / generic-network branches are
/// each observed without making a real S3 call.
/// </summary>
public sealed class BucketAccessCheckTests
{
    private const string Bucket = "demo-bucket";

    [Fact]
    public async Task Pass_includes_resolved_region()
    {
        var client = new FakeS3Client
        {
            LocationResponse = new GetBucketLocationResponse
            {
                Location = new S3Region("eu-west-1"),
            },
        };

        var result = await new BucketAccessCheck(client, Bucket).RunAsync(CancellationToken.None);

        Assert.Equal(DoctorCheckStatus.Pass, result.Status);
        Assert.Contains("eu-west-1", result.Message);
        Assert.Contains(Bucket, result.Message);
    }

    [Fact]
    public async Task Empty_location_string_is_reported_as_us_east_1()
    {
        // GetBucketLocation returns an empty Location element for buckets in
        // us-east-1 — the doctor must surface "us-east-1" rather than a blank string.
        var client = new FakeS3Client
        {
            LocationResponse = new GetBucketLocationResponse { Location = new S3Region("") },
        };

        var result = await new BucketAccessCheck(client, Bucket).RunAsync(CancellationToken.None);

        Assert.Equal(DoctorCheckStatus.Pass, result.Status);
        Assert.Contains("us-east-1", result.Message);
    }

    [Fact]
    public async Task Fail_when_bucket_is_not_found()
    {
        var client = new FakeS3Client
        {
            LocationException = new AmazonS3Exception("not found")
            {
                StatusCode = HttpStatusCode.NotFound,
            },
        };

        var result = await new BucketAccessCheck(client, Bucket).RunAsync(CancellationToken.None);

        Assert.Equal(DoctorCheckStatus.Fail, result.Status);
        Assert.Contains("not found", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fail_when_access_is_denied()
    {
        var client = new FakeS3Client
        {
            LocationException = new AmazonS3Exception("forbidden")
            {
                StatusCode = HttpStatusCode.Forbidden,
                ErrorCode = "AccessDenied",
            },
        };

        var result = await new BucketAccessCheck(client, Bucket).RunAsync(CancellationToken.None);

        Assert.Equal(DoctorCheckStatus.Fail, result.Status);
        Assert.Contains("denied", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fail_on_generic_S3_error_carries_status_and_detail()
    {
        var client = new FakeS3Client
        {
            LocationException = new AmazonS3Exception("server error")
            {
                StatusCode = HttpStatusCode.InternalServerError,
                ErrorCode = "InternalError",
            },
        };

        var result = await new BucketAccessCheck(client, Bucket).RunAsync(CancellationToken.None);

        Assert.Equal(DoctorCheckStatus.Fail, result.Status);
        Assert.Contains("InternalError", result.Message);
        Assert.NotNull(result.Detail);
    }

    [Fact]
    public async Task Fail_on_HttpRequestException_reports_network_error()
    {
        var client = new FakeS3Client
        {
            LocationException = new HttpRequestException("DNS resolution failed"),
        };

        var result = await new BucketAccessCheck(client, Bucket).RunAsync(CancellationToken.None);

        Assert.Equal(DoctorCheckStatus.Fail, result.Status);
        Assert.Contains("DNS resolution failed", result.Message);
    }

    /// <summary>
    /// Test fake for <see cref="IAmazonS3"/>. Subclasses <see cref="AmazonS3Client"/>
    /// so the consumer doesn't have to stub out the ~80 methods on the interface;
    /// only the <c>GetBucketLocationAsync</c> seam used by <see cref="BucketAccessCheck"/>
    /// is overridden, and a dummy endpoint sidesteps real config validation.
    /// </summary>
    private sealed class FakeS3Client : AmazonS3Client
    {
        public GetBucketLocationResponse? LocationResponse { get; set; }
        public Exception? LocationException { get; set; }

        public FakeS3Client()
            : base(
                new BasicAWSCredentials("test", "test"),
                new AmazonS3Config { ServiceURL = "http://localhost:1" })
        {
        }

        public override Task<GetBucketLocationResponse> GetBucketLocationAsync(
            GetBucketLocationRequest request, CancellationToken cancellationToken = default)
        {
            if (LocationException is not null) throw LocationException;
            return Task.FromResult(LocationResponse ?? new GetBucketLocationResponse
            {
                Location = new S3Region(string.Empty),
            });
        }
    }
}
