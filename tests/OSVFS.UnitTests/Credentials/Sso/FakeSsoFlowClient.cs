using OSVFS.Credentials.Sso;
using OSVFS.ObjectStore;

namespace OSVFS.UnitTests.Credentials.Sso;

/// <summary>
/// In-memory <see cref="ISsoFlowClient"/> used by <see cref="SsoLoginServiceTests"/>.
/// Exposes per-method scriptable behavior so tests can assert sequencing, replicate
/// AuthorizationPending/SlowDown polling, and verify the final GetRoleCredentials call.
/// </summary>
internal sealed class FakeSsoFlowClient : ISsoFlowClient
{
    /// <summary>Number of <c>RegisterClient</c> calls observed.</summary>
    public int RegisterCalls { get; private set; }

    /// <summary>Number of <c>StartDeviceAuthorization</c> calls observed.</summary>
    public int StartDeviceCalls { get; private set; }

    /// <summary>Number of <c>CreateToken</c> calls observed (incl. pending/slow-down).</summary>
    public int CreateTokenCalls { get; private set; }

    /// <summary>Number of <c>GetRoleCredentials</c> calls observed.</summary>
    public int GetRoleCredentialsCalls { get; private set; }

    /// <summary>Last access token observed by <c>GetRoleCredentials</c>.</summary>
    public string? LastAccessTokenPresentedToSso { get; private set; }

    /// <summary>Whether <see cref="Dispose"/> has been called.</summary>
    public bool Disposed { get; private set; }

    /// <summary>Static client registration handed back from <c>RegisterClient</c>.</summary>
    public SsoClientRegistration RegistrationToReturn { get; set; } = new()
    {
        ClientId = "client-id",
        ClientSecret = "client-secret",
        ClientSecretExpiresAt = DateTimeOffset.UnixEpoch + TimeSpan.FromDays(90),
    };

    /// <summary>Static device-authorization payload handed back from <c>StartDeviceAuthorization</c>.</summary>
    public SsoDeviceAuthorization DeviceAuthorizationToReturn { get; set; } = new()
    {
        DeviceCode = "device-code",
        UserCode = "ABC-DEF",
        VerificationUri = "https://example.test/verify",
        VerificationUriComplete = "https://example.test/verify?user_code=ABC-DEF",
        ExpiresIn = TimeSpan.FromMinutes(10),
        PollInterval = TimeSpan.FromSeconds(5),
    };

    /// <summary>FIFO sequence of CreateToken outcomes (<see cref="SsoAccessToken"/> = success, exception types signal pending/slow-down/expired).</summary>
    public Queue<object> CreateTokenSequence { get; } = new();

    /// <summary>Static role credential handed back from <c>GetRoleCredentials</c>.</summary>
    public AwsCredential RoleCredentialToReturn { get; set; } = new()
    {
        AccessKeyId = "AKIAEXAMPLE",
        SecretAccessKey = "secret-example",
        SessionToken = "session-example",
    };

    /// <inheritdoc/>
    public Task<SsoClientRegistration> RegisterClientAsync(string clientName, CancellationToken cancellationToken)
    {
        RegisterCalls++;
        return Task.FromResult(RegistrationToReturn);
    }

    /// <inheritdoc/>
    public Task<SsoDeviceAuthorization> StartDeviceAuthorizationAsync(
        SsoClientRegistration registration, string startUrl, CancellationToken cancellationToken)
    {
        StartDeviceCalls++;
        return Task.FromResult(DeviceAuthorizationToReturn);
    }

    /// <inheritdoc/>
    public Task<SsoAccessToken> CreateTokenAsync(
        SsoClientRegistration registration, string deviceCode, CancellationToken cancellationToken)
    {
        CreateTokenCalls++;
        if (CreateTokenSequence.Count == 0)
        {
            throw new InvalidOperationException("CreateTokenSequence is empty — test forgot to enqueue an outcome.");
        }
        var next = CreateTokenSequence.Dequeue();
        return next switch
        {
            SsoAccessToken token => Task.FromResult(token),
            Exception ex => Task.FromException<SsoAccessToken>(ex),
            _ => throw new InvalidOperationException($"Unsupported CreateToken outcome '{next.GetType()}'."),
        };
    }

    /// <inheritdoc/>
    public Task<AwsCredential> GetRoleCredentialsAsync(
        string accessToken, string accountId, string roleName, CancellationToken cancellationToken)
    {
        GetRoleCredentialsCalls++;
        LastAccessTokenPresentedToSso = accessToken;
        return Task.FromResult(RoleCredentialToReturn);
    }

    /// <inheritdoc/>
    public void Dispose() => Disposed = true;
}
