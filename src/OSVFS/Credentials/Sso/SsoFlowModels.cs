namespace OSVFS.Credentials.Sso;

/// <summary>
/// OIDC client registration returned by <c>RegisterClient</c>; reused across token
/// exchanges and cached until <see cref="ClientSecretExpiresAt"/> elapses.
/// </summary>
internal sealed record SsoClientRegistration
{
    /// <summary>OIDC client identifier issued by IAM Identity Center.</summary>
    public required string ClientId { get; init; }

    /// <summary>OIDC client secret paired with <see cref="ClientId"/>.</summary>
    public required string ClientSecret { get; init; }

    /// <summary>Wall-clock expiration of the client secret (Unix seconds, may be null).</summary>
    public required DateTimeOffset? ClientSecretExpiresAt { get; init; }
}

/// <summary>
/// Device-authorization grant produced by <c>StartDeviceAuthorization</c>.
/// </summary>
internal sealed record SsoDeviceAuthorization
{
    /// <summary>Opaque device code echoed back when polling <c>CreateToken</c>.</summary>
    public required string DeviceCode { get; init; }

    /// <summary>Short user-facing code shown if the user lands on the bare verification URL.</summary>
    public required string UserCode { get; init; }

    /// <summary>Browser URL the user must open and approve.</summary>
    public required string VerificationUri { get; init; }

    /// <summary>Convenience URL that pre-fills the user code.</summary>
    public required string VerificationUriComplete { get; init; }

    /// <summary>How long the device code remains valid before the user must restart.</summary>
    public required TimeSpan ExpiresIn { get; init; }

    /// <summary>Server-recommended polling interval; clamped to a sane minimum by the caller.</summary>
    public required TimeSpan PollInterval { get; init; }
}

/// <summary>
/// Bearer token returned by <c>CreateToken</c> after the user approves the device flow.
/// </summary>
internal sealed record SsoAccessToken
{
    /// <summary>OAuth bearer token used to call SSO portal APIs.</summary>
    public required string AccessToken { get; init; }

    /// <summary>Wall-clock expiration of <see cref="AccessToken"/>.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}
