using Testcontainers.Azurite;
using Xunit;

namespace OSVFS.Core.IntegrationTests;

/// <summary>
/// Boots an Azurite container once per test class collection so suites can share the
/// startup cost. Each test creates its own container under the same Azurite instance
/// to keep tests independent.
/// </summary>
public sealed class AzuriteFixture : IAsyncLifetime
{
    /// <summary>
    /// Default Azurite well-known account that the official image preconfigures.
    /// Connection strings constructed from this account work against any Azurite
    /// container started with the default settings.
    /// </summary>
    public const string AccountName = "devstoreaccount1";

    // Use the floating "latest" Azurite tag, and pass --skipApiVersionCheck so a
    // newer Azure.Storage.Blobs SDK (whose default x-ms-version Azurite has not
    // shipped support for yet) does not break the IT with InvalidHeaderValue.
    // WithCommand replaces — rather than appends to — the builder's default
    // command, so the --blobHost / --queueHost / --tableHost flags from
    // AzuriteBuilder.Init() have to be re-specified here.
    private readonly AzuriteContainer container =
        new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:latest")
            .WithCommand(
                "--blobHost", "0.0.0.0",
                "--queueHost", "0.0.0.0",
                "--tableHost", "0.0.0.0",
                "--skipApiVersionCheck")
            .Build();

    /// <summary>
    /// Connection string handed back by the running Azurite container. Carries the
    /// dynamically-mapped Blob endpoint so tests do not have to hard-code 10000.
    /// </summary>
    public string ConnectionString => container.GetConnectionString();

    public async Task InitializeAsync() => await container.StartAsync();

    public async Task DisposeAsync() => await container.DisposeAsync();
}

[CollectionDefinition(AzuriteCollection.Name)]
public sealed class AzuriteCollection : ICollectionFixture<AzuriteFixture>
{
    public const string Name = "Azurite";
}
