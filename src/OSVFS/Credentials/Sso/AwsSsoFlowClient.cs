using Amazon;
using Amazon.Runtime;
using Amazon.SSO;
using Amazon.SSO.Model;
using Amazon.SSOOIDC;
using Amazon.SSOOIDC.Model;
using OSVFS.ObjectStore;

namespace OSVFS.Credentials.Sso;

/// <summary>
/// Production <see cref="ISsoFlowClient"/> backed by <see cref="AmazonSSOOIDCClient"/>
/// and <see cref="AmazonSSOClient"/>. Both APIs are anonymous (the bearer token rides
/// on the request body), so the SDK clients are constructed with anonymous credentials.
/// </summary>
internal sealed class AwsSsoFlowClient : ISsoFlowClient
{
    /// <summary>OAuth grant type identifier for the device-code flow.</summary>
    private const string DeviceCodeGrantType = "urn:ietf:params:oauth:grant-type:device_code";

    /// <summary>Default polling interval used when the server does not return one.</summary>
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);

    /// <summary>Default device-code lifetime used when the server does not return one.</summary>
    private static readonly TimeSpan DefaultExpiresIn = TimeSpan.FromMinutes(10);

    private readonly AmazonSSOOIDCClient oidcClient;
    private readonly AmazonSSOClient ssoClient;
    private readonly TimeProvider timeProvider;

    /// <summary>
    /// Builds the wrapper around freshly-constructed SDK clients pinned to the SSO region.
    /// </summary>
    public AwsSsoFlowClient(string region, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        ArgumentNullException.ThrowIfNull(timeProvider);
        this.timeProvider = timeProvider;

        var endpoint = RegionEndpoint.GetBySystemName(region);
        var anonymous = new AnonymousAWSCredentials();
        oidcClient = new AmazonSSOOIDCClient(anonymous, new AmazonSSOOIDCConfig { RegionEndpoint = endpoint });
        ssoClient = new AmazonSSOClient(anonymous, new AmazonSSOConfig { RegionEndpoint = endpoint });
    }

    /// <inheritdoc/>
    public async Task<SsoClientRegistration> RegisterClientAsync(
        string clientName, CancellationToken cancellationToken)
    {
        try
        {
            var response = await oidcClient.RegisterClientAsync(
                new RegisterClientRequest
                {
                    ClientName = clientName,
                    ClientType = "public",
                },
                cancellationToken).ConfigureAwait(false);

            return new SsoClientRegistration
            {
                ClientId = response.ClientId,
                ClientSecret = response.ClientSecret,
                ClientSecretExpiresAt = response.ClientSecretExpiresAt is { } expiresAt and > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(expiresAt)
                    : null,
            };
        }
        catch (AmazonSSOOIDCException ex)
        {
            throw new SsoLoginException($"RegisterClient failed: {ex.Message}", ex);
        }
    }

    /// <inheritdoc/>
    public async Task<SsoDeviceAuthorization> StartDeviceAuthorizationAsync(
        SsoClientRegistration registration, string startUrl, CancellationToken cancellationToken)
    {
        try
        {
            var response = await oidcClient.StartDeviceAuthorizationAsync(
                new StartDeviceAuthorizationRequest
                {
                    ClientId = registration.ClientId,
                    ClientSecret = registration.ClientSecret,
                    StartUrl = startUrl,
                },
                cancellationToken).ConfigureAwait(false);

            return new SsoDeviceAuthorization
            {
                DeviceCode = response.DeviceCode,
                UserCode = response.UserCode,
                VerificationUri = response.VerificationUri,
                VerificationUriComplete = response.VerificationUriComplete,
                ExpiresIn = response.ExpiresIn is { } expires and > 0
                    ? TimeSpan.FromSeconds(expires)
                    : DefaultExpiresIn,
                PollInterval = response.Interval is { } interval and > 0
                    ? TimeSpan.FromSeconds(interval)
                    : DefaultPollInterval,
            };
        }
        catch (AmazonSSOOIDCException ex)
        {
            throw new SsoLoginException($"StartDeviceAuthorization failed: {ex.Message}", ex);
        }
    }

    /// <inheritdoc/>
    public async Task<SsoAccessToken> CreateTokenAsync(
        SsoClientRegistration registration, string deviceCode, CancellationToken cancellationToken)
    {
        try
        {
            var response = await oidcClient.CreateTokenAsync(
                new CreateTokenRequest
                {
                    ClientId = registration.ClientId,
                    ClientSecret = registration.ClientSecret,
                    GrantType = DeviceCodeGrantType,
                    DeviceCode = deviceCode,
                },
                cancellationToken).ConfigureAwait(false);

            var lifetime = response.ExpiresIn is { } seconds and > 0
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.FromHours(1);
            return new SsoAccessToken
            {
                AccessToken = response.AccessToken,
                ExpiresAt = timeProvider.GetUtcNow() + lifetime,
            };
        }
        catch (AuthorizationPendingException)
        {
            throw new SsoAuthorizationPendingException();
        }
        catch (Amazon.SSOOIDC.Model.SlowDownException)
        {
            throw new SsoSlowDownException();
        }
        catch (Amazon.SSOOIDC.Model.ExpiredTokenException)
        {
            throw new SsoExpiredTokenException();
        }
        catch (AmazonSSOOIDCException ex)
        {
            throw new SsoLoginException($"CreateToken failed: {ex.Message}", ex);
        }
    }

    /// <inheritdoc/>
    public async Task<AwsCredential> GetRoleCredentialsAsync(
        string accessToken, string accountId, string roleName, CancellationToken cancellationToken)
    {
        try
        {
            var response = await ssoClient.GetRoleCredentialsAsync(
                new GetRoleCredentialsRequest
                {
                    AccessToken = accessToken,
                    AccountId = accountId,
                    RoleName = roleName,
                },
                cancellationToken).ConfigureAwait(false);

            var creds = response.RoleCredentials
                ?? throw new SsoLoginException("GetRoleCredentials returned an empty payload.");
            return new AwsCredential
            {
                AccessKeyId = creds.AccessKeyId,
                SecretAccessKey = creds.SecretAccessKey,
                SessionToken = creds.SessionToken,
            };
        }
        catch (AmazonSSOException ex)
        {
            throw new SsoLoginException($"GetRoleCredentials failed: {ex.Message}", ex);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        oidcClient.Dispose();
        ssoClient.Dispose();
    }
}
