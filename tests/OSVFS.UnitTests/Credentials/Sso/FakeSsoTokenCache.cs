using OSVFS.Credentials.Sso;

namespace OSVFS.UnitTests.Credentials.Sso;

/// <summary>
/// In-memory <see cref="ISsoTokenCache"/> backing for tests. Mirrors the contract
/// (per-startUrl key, replace on save, return null on miss) so the service can be
/// exercised without hitting the Windows Credential Manager.
/// </summary>
internal sealed class FakeSsoTokenCache : ISsoTokenCache
{
    private readonly Dictionary<string, SsoCachedToken> entries = new(StringComparer.Ordinal);

    /// <summary>Snapshot of the currently stored entries, keyed by start URL.</summary>
    public IReadOnlyDictionary<string, SsoCachedToken> Entries => entries;

    /// <inheritdoc/>
    public SsoCachedToken? Load(string startUrl) =>
        entries.TryGetValue(startUrl, out var token) ? token : null;

    /// <inheritdoc/>
    public void Save(string startUrl, SsoCachedToken token) => entries[startUrl] = token;

    /// <inheritdoc/>
    public bool Delete(string startUrl) => entries.Remove(startUrl);
}
