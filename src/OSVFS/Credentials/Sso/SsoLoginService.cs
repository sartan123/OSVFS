using OSVFS.ObjectStore;

namespace OSVFS.Credentials.Sso;

/// <summary>
/// Drives the IAM Identity Center device-authorization flow end-to-end:
/// reuses a cached bearer token when one is still valid, otherwise registers a
/// new OIDC client, opens the browser, polls <c>CreateToken</c>, exchanges the
/// resulting bearer token for short-term role credentials, and persists those
/// credentials under the requested OSVFS profile.
/// </summary>
internal sealed class SsoLoginService
{
    /// <summary>Buffer kept around the access-token expiry to avoid race-y reuse.</summary>
    private static readonly TimeSpan ExpirySafetyMargin = TimeSpan.FromMinutes(1);

    /// <summary>How much to extend the polling interval after a SlowDown response.</summary>
    private static readonly TimeSpan SlowDownIncrement = TimeSpan.FromSeconds(5);

    private readonly ISsoFlowClient flowClient;
    private readonly ISsoTokenCache tokenCache;
    private readonly IAwsCredentialStore credentialStore;
    private readonly IBrowserLauncher browserLauncher;
    private readonly TimeProvider timeProvider;
    private readonly TextWriter output;
    private readonly TimeSpan minPollInterval;

    /// <summary>
    /// Constructs the service with all collaborators wired in. Production callers use
    /// <see cref="AwsSsoFlowClient"/>, <see cref="WindowsSsoTokenCache"/>,
    /// <see cref="DefaultBrowserLauncher"/>, and <see cref="TimeProvider.System"/>.
    /// <paramref name="minPollInterval"/> caps how aggressively the polling loop hits
    /// <c>CreateToken</c> when the server reports a sub-second cadence; tests pass
    /// <see cref="TimeSpan.Zero"/> to avoid real-time waits.
    /// </summary>
    public SsoLoginService(
        ISsoFlowClient flowClient,
        ISsoTokenCache tokenCache,
        IAwsCredentialStore credentialStore,
        IBrowserLauncher browserLauncher,
        TimeProvider timeProvider,
        TextWriter output,
        TimeSpan? minPollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(flowClient);
        ArgumentNullException.ThrowIfNull(tokenCache);
        ArgumentNullException.ThrowIfNull(credentialStore);
        ArgumentNullException.ThrowIfNull(browserLauncher);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(output);

        this.flowClient = flowClient;
        this.tokenCache = tokenCache;
        this.credentialStore = credentialStore;
        this.browserLauncher = browserLauncher;
        this.timeProvider = timeProvider;
        this.output = output;
        this.minPollInterval = minPollInterval ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>
    /// Runs the device-authorization flow for <paramref name="parameters"/> and returns
    /// the short-term credentials that were saved to the credential store.
    /// </summary>
    public async Task<AwsCredential> LoginAsync(SsoLoginParameters parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var accessToken = await ObtainAccessTokenAsync(parameters.StartUrl, cancellationToken).ConfigureAwait(false);

        output.WriteLine($"Fetching role credentials for account {parameters.AccountId}, role {parameters.RoleName}...");
        var credential = await flowClient.GetRoleCredentialsAsync(
            accessToken, parameters.AccountId, parameters.RoleName, cancellationToken).ConfigureAwait(false);

        credentialStore.Save(parameters.ProfileName, credential);
        output.WriteLine($"Saved profile '{parameters.ProfileName}'.");
        return credential;
    }

    /// <summary>
    /// Returns a usable bearer token, either from the cache (if still valid) or by
    /// running the full device-authorization flow and updating the cache.
    /// </summary>
    private async Task<string> ObtainAccessTokenAsync(string startUrl, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var cached = tokenCache.Load(startUrl);
        if (cached is not null && cached.AccessTokenExpiresAt - ExpirySafetyMargin > now)
        {
            output.WriteLine($"Reusing cached SSO access token (expires {cached.AccessTokenExpiresAt:u}).");
            return cached.AccessToken;
        }

        var registration = ResolveRegistration(cached, now)
            ?? await flowClient.RegisterClientAsync("OSVFS", cancellationToken).ConfigureAwait(false);

        var authorization = await flowClient.StartDeviceAuthorizationAsync(
            registration, startUrl, cancellationToken).ConfigureAwait(false);

        output.WriteLine($"Opening {authorization.VerificationUriComplete} in your browser...");
        output.WriteLine($"If the browser does not open, navigate to {authorization.VerificationUri} and enter code {authorization.UserCode}.");
        try
        {
            browserLauncher.Launch(authorization.VerificationUriComplete);
        }
        catch (Exception ex)
        {
            output.WriteLine($"(could not auto-launch browser: {ex.Message})");
        }

        var token = await PollForTokenAsync(registration, authorization, cancellationToken).ConfigureAwait(false);

        tokenCache.Save(startUrl, new SsoCachedToken
        {
            ClientId = registration.ClientId,
            ClientSecret = registration.ClientSecret,
            ClientSecretExpiresAt = registration.ClientSecretExpiresAt,
            AccessToken = token.AccessToken,
            AccessTokenExpiresAt = token.ExpiresAt,
        });
        return token.AccessToken;
    }

    /// <summary>
    /// Returns the cached client registration when its secret is still valid; otherwise
    /// returns null so the caller registers a fresh client.
    /// </summary>
    private static SsoClientRegistration? ResolveRegistration(SsoCachedToken? cached, DateTimeOffset now)
    {
        if (cached is null) return null;
        if (cached.ClientSecretExpiresAt is { } expiry && expiry - ExpirySafetyMargin <= now) return null;
        return new SsoClientRegistration
        {
            ClientId = cached.ClientId,
            ClientSecret = cached.ClientSecret,
            ClientSecretExpiresAt = cached.ClientSecretExpiresAt,
        };
    }

    /// <summary>
    /// Polls <c>CreateToken</c> at the server-suggested cadence until the user approves,
    /// the device code expires, or <paramref name="cancellationToken"/> fires.
    /// </summary>
    private async Task<SsoAccessToken> PollForTokenAsync(
        SsoClientRegistration registration,
        SsoDeviceAuthorization authorization,
        CancellationToken cancellationToken)
    {
        var deadline = timeProvider.GetUtcNow() + authorization.ExpiresIn;
        var interval = authorization.PollInterval < minPollInterval ? minPollInterval : authorization.PollInterval;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await flowClient.CreateTokenAsync(
                    registration, authorization.DeviceCode, cancellationToken).ConfigureAwait(false);
            }
            catch (SsoAuthorizationPendingException)
            {
                // Expected before the user clicks "Approve" — fall through to delay.
            }
            catch (SsoSlowDownException)
            {
                interval += SlowDownIncrement;
            }

            if (timeProvider.GetUtcNow() + interval >= deadline)
            {
                throw new SsoExpiredTokenException();
            }
            await Task.Delay(interval, timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }
}
