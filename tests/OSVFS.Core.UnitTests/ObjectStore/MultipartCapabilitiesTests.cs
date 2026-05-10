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
    public void For_falls_back_to_S3_for_not_yet_implemented_providers()
    {
        // GCS and AzureBlob backends haven't landed yet — until they do, the
        // safe-default capabilities the validator applies should still be the
        // conservative S3 bounds. The fallback also keeps the validator from
        // crashing on a Provider value that has been added to the enum but
        // does not yet have a dedicated capabilities row.
        Assert.Same(MultipartCapabilities.S3, MultipartCapabilities.For(ObjectStoreProvider.Gcs));
        Assert.Same(MultipartCapabilities.S3, MultipartCapabilities.For(ObjectStoreProvider.AzureBlob));
    }
}
