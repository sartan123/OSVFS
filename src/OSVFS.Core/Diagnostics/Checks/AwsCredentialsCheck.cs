using Amazon.Runtime;

namespace OSVFS.Diagnostics.Checks;

/// <summary>
/// Resolves an <see cref="AWSCredentials"/> instance to immutable values via
/// <c>GetCredentialsAsync</c>. A successful resolution proves the credential
/// source (env vars, profile, IMDS, OSVFS DPAPI store) is reachable; a failure
/// surfaces the original exception so the operator can tell which link in the
/// chain broke. The check also reports whether a session token is present
/// (i.e. temporary credentials) and the resolver type, which is the closest
/// approximation of "AssumeRole 失効まであと何分か" the SDK exposes without
/// reflecting on internal refresh state.
/// </summary>
internal sealed class AwsCredentialsCheck : IDoctorCheck
{
    private readonly AWSCredentials credentials;
    private readonly string source;

    /// <inheritdoc/>
    public string Name => "AWS credentials resolution";

    /// <summary>
    /// Constructs the check around an already-resolved <see cref="AWSCredentials"/>
    /// instance. <paramref name="source"/> is a human-readable label (e.g.
    /// "OSVFS profile 'default'", "SDK default chain") that flows into the
    /// success message so the operator knows which credential source was used.
    /// </summary>
    public AwsCredentialsCheck(AWSCredentials credentials, string source)
    {
        this.credentials = credentials;
        this.source = source;
    }

    /// <inheritdoc/>
    public async Task<DoctorResult> RunAsync(CancellationToken ct)
    {
        ImmutableCredentials immutable;
        try
        {
            immutable = await credentials.GetCredentialsAsync().ConfigureAwait(false);
        }
        catch (AmazonClientException ex)
        {
            return new DoctorResult(
                Name,
                DoctorCheckStatus.Fail,
                $"Could not resolve AWS credentials from {source}: {ex.Message}",
                ex.ToString());
        }

        // Surface the access key id's last 4 chars only. Full keys stay private but
        // the suffix is enough for the operator to confirm "yes, that's the IAM
        // user / role I expect to be acting as".
        var accessKeyTail = immutable.AccessKey.Length <= 4
            ? immutable.AccessKey
            : immutable.AccessKey[^4..];
        var temporary = immutable.UseToken;
        var resolverType = credentials.GetType().Name;

        var msg = temporary
            ? $"Resolved temporary credentials from {source} (AccessKey ...{accessKeyTail}, resolver {resolverType}). Refresh is handled by the SDK."
            : $"Resolved long-lived credentials from {source} (AccessKey ...{accessKeyTail}, resolver {resolverType}).";
        return new DoctorResult(Name, DoctorCheckStatus.Pass, msg);
    }
}
