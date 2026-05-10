using System.Text.Json.Serialization;

namespace OSVFS.Credentials.Sso;

/// <summary>
/// On-disk shape of <see cref="SsoCachedToken"/>; serialized as JSON, DPAPI-encrypted,
/// and stored in the Credential Manager blob field. Wall-clock fields are persisted as
/// Unix-epoch seconds so the format survives time-zone or DateTime locality drift.
/// </summary>
internal sealed record SsoTokenCachePayload
{
    /// <summary>OIDC client identifier.</summary>
    [JsonPropertyName("client_id")]
    public required string ClientId { get; init; }

    /// <summary>OIDC client secret.</summary>
    [JsonPropertyName("client_secret")]
    public required string ClientSecret { get; init; }

    /// <summary>Client-secret expiration, Unix epoch seconds; null when unknown.</summary>
    [JsonPropertyName("client_secret_expires_at_unix")]
    public long? ClientSecretExpiresAtUnix { get; init; }

    /// <summary>OAuth bearer token.</summary>
    [JsonPropertyName("access_token")]
    public required string AccessToken { get; init; }

    /// <summary>Access-token expiration, Unix epoch seconds.</summary>
    [JsonPropertyName("access_token_expires_at_unix")]
    public required long AccessTokenExpiresAtUnix { get; init; }
}

/// <summary>
/// Source-generated JSON context for <see cref="SsoTokenCachePayload"/>; required so the
/// AOT-published binary can serialize without reflection.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(SsoTokenCachePayload))]
internal sealed partial class SsoTokenCacheJsonContext : JsonSerializerContext;
