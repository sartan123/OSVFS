namespace OSVFS.Credentials.Sso;

/// <summary>
/// Persistence for the IAM Identity Center bearer token issued by the device flow.
/// Reusing a still-valid token across runs lets the user skip the browser prompt.
/// </summary>
internal interface ISsoTokenCache
{
    /// <summary>
    /// Loads the cached token for <paramref name="startUrl"/>, or null when no entry exists.
    /// Callers are responsible for checking <see cref="SsoCachedToken.AccessTokenExpiresAt"/>.
    /// </summary>
    SsoCachedToken? Load(string startUrl);

    /// <summary>
    /// Persists <paramref name="token"/> for <paramref name="startUrl"/>, replacing any prior entry.
    /// </summary>
    void Save(string startUrl, SsoCachedToken token);

    /// <summary>
    /// Removes the cache entry for <paramref name="startUrl"/>; returns false when none existed.
    /// </summary>
    bool Delete(string startUrl);
}

/// <summary>
/// Cached IAM Identity Center material: the OIDC client registration and the bearer token.
/// </summary>
internal sealed record SsoCachedToken
{
    /// <summary>OIDC client identifier issued by RegisterClient.</summary>
    public required string ClientId { get; init; }

    /// <summary>OIDC client secret paired with <see cref="ClientId"/>.</summary>
    public required string ClientSecret { get; init; }

    /// <summary>Wall-clock expiration of the client secret; null when the API didn't return one.</summary>
    public required DateTimeOffset? ClientSecretExpiresAt { get; init; }

    /// <summary>Bearer token returned by CreateToken.</summary>
    public required string AccessToken { get; init; }

    /// <summary>Wall-clock expiration of <see cref="AccessToken"/>.</summary>
    public required DateTimeOffset AccessTokenExpiresAt { get; init; }
}
