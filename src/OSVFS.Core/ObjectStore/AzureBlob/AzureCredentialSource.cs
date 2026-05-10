using Azure.Core;

namespace OSVFS.ObjectStore.AzureBlob;

/// <summary>
/// Azure-side counterpart of <see cref="AwsCredentialSource"/>. Carries the
/// resolution path the host picked through the provider-neutral
/// <see cref="IObjectStoreCredentialSource"/> seam and surfaces the concrete
/// data the backend needs to construct the SDK clients.
/// </summary>
/// <remarks>
/// The four supported branches — connection string, SAS, Managed Identity,
/// <c>DefaultAzureCredential</c> — are mutually exclusive. The factory
/// (<c>MountOptionsBuilder.ResolveAzureCredential</c>) picks exactly one
/// based on which keys are present in <c>osvfs.toml</c>.
/// </remarks>
internal sealed class AzureCredentialSource : IObjectStoreCredentialSource
{
    /// <summary>
    /// Connection string carrying account name + key + endpoints, when this
    /// source represents the connection-string branch. Null on every other
    /// branch.
    /// </summary>
    public string? ConnectionString { get; }

    /// <summary>
    /// Storage account name (the <c>{accountName}</c> in
    /// <c>https://{accountName}.blob.core.windows.net</c>). Null on the
    /// connection-string branch (the connection string already names the
    /// account); set on every other branch so the backend can build the
    /// service endpoint URL.
    /// </summary>
    public string? AccountName { get; }

    /// <summary>
    /// Service- or account-level shared access signature, when this source
    /// represents the SAS branch. Null on every other branch.
    /// </summary>
    public string? Sas { get; }

    /// <summary>
    /// Live <see cref="TokenCredential"/> the SDK uses to fetch bearer tokens
    /// (Managed Identity / <c>DefaultAzureCredential</c> branches). Null on
    /// the connection-string and SAS branches.
    /// </summary>
    public TokenCredential? TokenCredential { get; }

    /// <summary>
    /// Human-readable description of the resolution path (e.g.
    /// <c>"connection string"</c>, <c>"SAS for 'myaccount'"</c>,
    /// <c>"Managed Identity for 'myaccount'"</c>,
    /// <c>"DefaultAzureCredential chain for 'myaccount'"</c>). Surfaced by
    /// the doctor and the mount-startup log message; mirrors the AWS-side
    /// wording so multi-cloud logs stay consistent.
    /// </summary>
    public string Description { get; }

    private AzureCredentialSource(
        string? connectionString,
        string? accountName,
        string? sas,
        TokenCredential? tokenCredential,
        string description)
    {
        ConnectionString = connectionString;
        AccountName = accountName;
        Sas = sas;
        TokenCredential = tokenCredential;
        Description = description;
    }

    /// <summary>
    /// Wraps an Azure Storage connection string. The connection string is
    /// what Azurite hands out by default (<c>UseDevelopmentStorage=true</c>)
    /// and what most operators paste from the Azure Portal "Access keys"
    /// blade, so it is the lowest-friction starting point.
    /// </summary>
    public static AzureCredentialSource FromConnectionString(
        string connectionString, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        return new AzureCredentialSource(
            connectionString, accountName: null, sas: null, tokenCredential: null, description);
    }

    /// <summary>
    /// Wraps a service- or account-level SAS bound to
    /// <paramref name="accountName"/>. The backend builds the service
    /// endpoint as <c>https://{accountName}.blob.core.windows.net</c>.
    /// </summary>
    public static AzureCredentialSource FromSas(
        string accountName, string sas, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sas);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        return new AzureCredentialSource(
            connectionString: null, accountName, sas, tokenCredential: null, description);
    }

    /// <summary>
    /// Wraps a live <see cref="TokenCredential"/> bound to
    /// <paramref name="accountName"/>. Used by both the
    /// <c>ManagedIdentityCredential</c> and <c>DefaultAzureCredential</c>
    /// branches; the description distinguishes which one the operator picked.
    /// </summary>
    public static AzureCredentialSource FromTokenCredential(
        string accountName, TokenCredential tokenCredential, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        ArgumentNullException.ThrowIfNull(tokenCredential);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        return new AzureCredentialSource(
            connectionString: null, accountName, sas: null, tokenCredential, description);
    }
}
