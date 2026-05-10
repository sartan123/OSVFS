using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using OSVFS.Diagnostics;
using OSVFS.Diagnostics.Checks;
using Xunit;

namespace OSVFS.Core.UnitTests.Diagnostics;

/// <summary>
/// Drives <see cref="BucketVersioningCheck"/> through the three states the S3
/// API distinguishes: explicitly Enabled, explicitly Suspended, and the
/// "never configured" empty-status case. Suspended and never-configured both
/// fail because OSVFS requires versioning to be actively protecting writes.
/// </summary>
public sealed class BucketVersioningCheckTests
{
    private const string Bucket = "demo-bucket";

    [Fact]
    public async Task Pass_when_status_is_Enabled()
    {
        var client = new FakeS3Client
        {
            VersioningResponse = new GetBucketVersioningResponse
            {
                VersioningConfig = new S3BucketVersioningConfig
                {
                    Status = VersionStatus.Enabled,
                },
            },
        };

        var result = await new BucketVersioningCheck(client, Bucket).RunAsync(CancellationToken.None);

        Assert.Equal(DoctorCheckStatus.Pass, result.Status);
        Assert.Contains("enabled", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fail_when_status_is_Suspended()
    {
        var client = new FakeS3Client
        {
            VersioningResponse = new GetBucketVersioningResponse
            {
                VersioningConfig = new S3BucketVersioningConfig
                {
                    Status = VersionStatus.Suspended,
                },
            },
        };

        var result = await new BucketVersioningCheck(client, Bucket).RunAsync(CancellationToken.None);

        Assert.Equal(DoctorCheckStatus.Fail, result.Status);
        Assert.Contains("Suspended", result.Message);
        Assert.Contains("put-bucket-versioning", result.Message);
    }

    [Fact]
    public async Task Fail_when_versioning_was_never_configured()
    {
        // S3 returns no Status element until versioning has ever been touched.
        var client = new FakeS3Client
        {
            VersioningResponse = new GetBucketVersioningResponse
            {
                VersioningConfig = new S3BucketVersioningConfig(),
            },
        };

        var result = await new BucketVersioningCheck(client, Bucket).RunAsync(CancellationToken.None);

        Assert.Equal(DoctorCheckStatus.Fail, result.Status);
        Assert.Contains("never configured", result.Message);
    }

    [Fact]
    public async Task Fail_when_S3_call_throws()
    {
        var client = new FakeS3Client
        {
            VersioningException = new AmazonS3Exception("boom") { ErrorCode = "InternalError" },
        };

        var result = await new BucketVersioningCheck(client, Bucket).RunAsync(CancellationToken.None);

        Assert.Equal(DoctorCheckStatus.Fail, result.Status);
        Assert.Contains("InternalError", result.Message);
        Assert.NotNull(result.Detail);
    }

    private sealed class FakeS3Client : AmazonS3Client
    {
        public GetBucketVersioningResponse? VersioningResponse { get; set; }
        public Exception? VersioningException { get; set; }

        public FakeS3Client()
            : base(
                new BasicAWSCredentials("test", "test"),
                new AmazonS3Config { ServiceURL = "http://localhost:1" })
        {
        }

        public override Task<GetBucketVersioningResponse> GetBucketVersioningAsync(
            GetBucketVersioningRequest request, CancellationToken cancellationToken = default)
        {
            if (VersioningException is not null) throw VersioningException;
            return Task.FromResult(VersioningResponse ?? new GetBucketVersioningResponse());
        }
    }
}
