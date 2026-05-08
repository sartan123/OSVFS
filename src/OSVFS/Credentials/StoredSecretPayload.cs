using System.Text.Json.Serialization;

namespace OSVFS.Credentials;

/// <summary>
/// DPAPI-protected payload written into the Credential Manager blob field.
/// Only the secret half of the credential pair lives here; the access key id is
/// stored in the credential's UserName field for low-friction inspection.
/// </summary>
internal sealed record StoredSecretPayload
{
    /// <summary>
    /// AWS secret access key matching the credential's UserName.
    /// </summary>
    [JsonPropertyName("secret")]
    public required string SecretAccessKey { get; init; }

    /// <summary>
    /// Optional STS session token for temporary credentials.
    /// </summary>
    [JsonPropertyName("session")]
    public string? SessionToken { get; init; }
}

/// <summary>
/// Source-generated JSON context for <see cref="StoredSecretPayload"/>; required so the
/// AOT-published binary can serialize without reflection.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(StoredSecretPayload))]
internal sealed partial class StoredSecretJsonContext : JsonSerializerContext;
