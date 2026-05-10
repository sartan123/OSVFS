using Amazon.Runtime;
using OSVFS.Diagnostics;
using OSVFS.Diagnostics.Checks;
using Xunit;

namespace OSVFS.Core.UnitTests.Diagnostics;

/// <summary>
/// Exercises <see cref="AwsCredentialsCheck"/> against fake <see cref="AWSCredentials"/>
/// instances so the resolver-success and resolver-throws code paths can be observed
/// without making any network calls.
/// </summary>
public sealed class AwsCredentialsCheckTests
{
    [Fact]
    public async Task Pass_when_static_credentials_resolve()
    {
        var check = new AwsCredentialsCheck(
            new BasicAWSCredentials("AKIAEXAMPLE1234", "secret"),
            "test source");

        var result = await check.RunAsync(CancellationToken.None);

        Assert.Equal(DoctorCheckStatus.Pass, result.Status);
        Assert.Contains("...1234", result.Message);
        Assert.Contains("test source", result.Message);
        Assert.Contains("long-lived", result.Message);
    }

    [Fact]
    public async Task Pass_with_session_token_calls_out_temporary_credentials()
    {
        var check = new AwsCredentialsCheck(
            new SessionAWSCredentials("AKIATEMP123456", "secret", "session-tok"),
            "STS");

        var result = await check.RunAsync(CancellationToken.None);

        Assert.Equal(DoctorCheckStatus.Pass, result.Status);
        Assert.Contains("temporary credentials", result.Message);
    }

    [Fact]
    public async Task Fail_when_credentials_resolution_throws_AmazonClientException()
    {
        var check = new AwsCredentialsCheck(
            new ThrowingCredentials(new AmazonClientException("no creds available")),
            "broken source");

        var result = await check.RunAsync(CancellationToken.None);

        Assert.Equal(DoctorCheckStatus.Fail, result.Status);
        Assert.Contains("broken source", result.Message);
        Assert.Contains("no creds available", result.Message);
    }

    /// <summary>
    /// AWSCredentials wrapper that surfaces a configurable exception out of
    /// <see cref="AWSCredentials.GetCredentialsAsync"/>. Mimics the failure
    /// modes of the SDK chain (no profile, no env var, IMDS unreachable) without
    /// reaching for the network.
    /// </summary>
    private sealed class ThrowingCredentials : AWSCredentials
    {
        private readonly Exception ex;

        public ThrowingCredentials(Exception ex)
        {
            this.ex = ex;
        }

        public override ImmutableCredentials GetCredentials() => throw ex;

        public override Task<ImmutableCredentials> GetCredentialsAsync() => Task.FromException<ImmutableCredentials>(ex);
    }
}
