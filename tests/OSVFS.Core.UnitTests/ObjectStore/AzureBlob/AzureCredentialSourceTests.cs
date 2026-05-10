using Azure.Core;
using Azure.Identity;
using OSVFS.ObjectStore.AzureBlob;
using Xunit;

namespace OSVFS.Core.UnitTests.ObjectStore.AzureBlob;

/// <summary>
/// Verifies the four credential branches <see cref="AzureCredentialSource"/>
/// supports. Each factory method must populate exactly one branch and a
/// non-empty <see cref="AzureCredentialSource.Description"/>; the others
/// must stay null so the backend can switch on the populated branch
/// unambiguously.
/// </summary>
public class AzureCredentialSourceTests
{
    [Fact]
    public void FromConnectionString_populates_only_connection_string_branch()
    {
        var source = AzureCredentialSource.FromConnectionString(
            "DefaultEndpointsProtocol=https;AccountName=acct;AccountKey=k==;EndpointSuffix=core.windows.net",
            "Azure connection string");

        Assert.NotNull(source.ConnectionString);
        Assert.Null(source.AccountName);
        Assert.Null(source.Sas);
        Assert.Null(source.TokenCredential);
        Assert.Equal("Azure connection string", source.Description);
    }

    [Fact]
    public void FromSas_populates_account_name_and_sas_only()
    {
        var source = AzureCredentialSource.FromSas(
            accountName: "myaccount",
            sas: "sv=2024-01-01&...",
            description: "Azure SAS for 'myaccount'");

        Assert.Null(source.ConnectionString);
        Assert.Equal("myaccount", source.AccountName);
        Assert.Equal("sv=2024-01-01&...", source.Sas);
        Assert.Null(source.TokenCredential);
    }

    [Fact]
    public void FromTokenCredential_populates_account_name_and_token_only()
    {
        TokenCredential token = new DefaultAzureCredential();
        var source = AzureCredentialSource.FromTokenCredential(
            accountName: "myaccount",
            tokenCredential: token,
            description: "Azure DefaultAzureCredential chain for 'myaccount'");

        Assert.Null(source.ConnectionString);
        Assert.Equal("myaccount", source.AccountName);
        Assert.Null(source.Sas);
        Assert.Same(token, source.TokenCredential);
    }

    [Fact]
    public void FromConnectionString_rejects_empty_input()
    {
        Assert.Throws<ArgumentException>(() =>
            AzureCredentialSource.FromConnectionString("", "desc"));
    }

    [Fact]
    public void FromSas_rejects_empty_account_name()
    {
        Assert.Throws<ArgumentException>(() =>
            AzureCredentialSource.FromSas("", "sv=...", "desc"));
    }

    [Fact]
    public void FromSas_rejects_empty_sas()
    {
        Assert.Throws<ArgumentException>(() =>
            AzureCredentialSource.FromSas("acct", "", "desc"));
    }

    [Fact]
    public void FromTokenCredential_rejects_null_token()
    {
        Assert.Throws<ArgumentNullException>(() =>
            AzureCredentialSource.FromTokenCredential("acct", null!, "desc"));
    }
}
