using OSVFS.ObjectStore;

namespace OSVFS.Credentials;

/// <summary>
/// Persists AWS credentials per profile, encrypting the secret material at rest.
/// </summary>
internal interface IAwsCredentialStore
{
    /// <summary>
    /// Stores <paramref name="credential"/> under <paramref name="profileName"/>, replacing any prior entry.
    /// </summary>
    void Save(string profileName, AwsCredential credential);

    /// <summary>
    /// Returns the stored credential for <paramref name="profileName"/>, or null when no entry exists.
    /// </summary>
    AwsCredential? Load(string profileName);

    /// <summary>
    /// Deletes the entry for <paramref name="profileName"/>. Returns false when no entry existed.
    /// </summary>
    bool Delete(string profileName);

    /// <summary>
    /// Lists every profile name currently stored by this provider.
    /// </summary>
    IReadOnlyList<string> List();
}
