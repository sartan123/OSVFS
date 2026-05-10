using OSVFS.ObjectStore;

namespace OSVFS.Credentials.Sso;

/// <summary>
/// Abstraction over the IAM Identity Center / OIDC HTTP surface used by
/// <see cref="SsoLoginService"/>. The interface returns pure data records and
/// translates SDK exceptions into <see cref="SsoFlowExceptions"/> so the service
/// (and its tests) never need to import AWS SDK types.
/// </summary>
internal interface ISsoFlowClient : IDisposable
{
    /// <summary>
    /// Registers a public OIDC client for this OSVFS install.
    /// </summary>
    Task<SsoClientRegistration> RegisterClientAsync(string clientName, CancellationToken cancellationToken);

    /// <summary>
    /// Starts the device-authorization grant for <paramref name="startUrl"/>.
    /// </summary>
    Task<SsoDeviceAuthorization> StartDeviceAuthorizationAsync(
        SsoClientRegistration registration,
        string startUrl,
        CancellationToken cancellationToken);

    /// <summary>
    /// Exchanges the device code for a bearer token. Throws
    /// <see cref="SsoAuthorizationPendingException"/> until the user approves.
    /// </summary>
    Task<SsoAccessToken> CreateTokenAsync(
        SsoClientRegistration registration,
        string deviceCode,
        CancellationToken cancellationToken);

    /// <summary>
    /// Calls <c>GetRoleCredentials</c> against the SSO portal and returns the
    /// resulting short-term AWS credentials.
    /// </summary>
    Task<AwsCredential> GetRoleCredentialsAsync(
        string accessToken,
        string accountId,
        string roleName,
        CancellationToken cancellationToken);
}
