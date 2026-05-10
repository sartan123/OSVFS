using OSVFS.ObjectStore;
using Xunit;

namespace OSVFS.Core.UnitTests.ObjectStore;

public class MultipartCapabilitiesTests
{
    [Fact]
    public void S3_capabilities_match_documented_AWS_S3_limits()
    {
        Assert.Equal(5L * 1024 * 1024, MultipartCapabilities.S3.MinPartSizeBytes);
        Assert.Equal(5L * 1024 * 1024 * 1024, MultipartCapabilities.S3.MaxPartSizeBytes);
        Assert.Equal(10_000, MultipartCapabilities.S3.MaxPartCount);
    }

    [Fact]
    public void For_returns_S3_capabilities_for_S3_provider()
    {
        Assert.Same(MultipartCapabilities.S3, MultipartCapabilities.For(ObjectStoreProvider.S3));
    }

    [Fact]
    public void AzureBlob_capabilities_match_documented_Block_Blob_limits()
    {
        // Azure Block Blob: blocks may be 1 byte to 4000 MiB; up to 50 000 blocks per blob.
        Assert.Equal(1L, MultipartCapabilities.AzureBlob.MinPartSizeBytes);
        Assert.Equal(4000L * 1024 * 1024, MultipartCapabilities.AzureBlob.MaxPartSizeBytes);
        Assert.Equal(50_000, MultipartCapabilities.AzureBlob.MaxPartCount);
    }

    [Fact]
    public void For_returns_AzureBlob_capabilities_for_AzureBlob_provider()
    {
        Assert.Same(
            MultipartCapabilities.AzureBlob,
            MultipartCapabilities.For(ObjectStoreProvider.AzureBlob));
    }

    [Fact]
    public void For_falls_back_to_S3_for_not_yet_implemented_providers()
    {
        // GCS hasn't landed yet — until it does, the safe-default capabilities
        // the validator applies should still be the conservative S3 bounds.
        Assert.Same(MultipartCapabilities.S3, MultipartCapabilities.For(ObjectStoreProvider.Gcs));
    }
}
