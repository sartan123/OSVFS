using OSVFS.Diagnostics;
using Xunit;

namespace OSVFS.UnitTests.Diagnostics;

/// <summary>
/// Smoke tests for the two ProjFS doctor checks. They only run on a host where
/// PrjFlt is registered (the OSVFS tests already require this elsewhere) and
/// assert that the check returns a fresh, non-throwing result.
/// </summary>
public sealed class ProjFsChecksTests
{
    [Fact]
    public async Task ProjFsFeatureCheck_passes_when_ProjFS_optional_feature_is_installed()
    {
        var result = await new ProjFsFeatureCheck().RunAsync(CancellationToken.None);

        Assert.Equal("Windows ProjFS feature (Client-ProjFS)", result.Name);
        // The build host is expected to have ProjFS enabled — the rest of the
        // OSVFS test suite (AdsMetadataStore, WatchSetSeeder, …) can't run
        // without it. Surface a clear assertion so a developer running the
        // suite on a non-ProjFS box knows what to do.
        Assert.True(
            result.Status == DoctorCheckStatus.Pass,
            $"ProjFS feature check failed unexpectedly: {result.Message}");
    }

    [Fact]
    public async Task ProjFsStartCheck_starts_and_stops_a_temp_virtualization_root()
    {
        var result = await new ProjFsStartCheck().RunAsync(CancellationToken.None);

        Assert.Equal("ProjFS StartVirtualizing smoke test", result.Name);
        Assert.True(
            result.Status == DoctorCheckStatus.Pass,
            $"ProjFS start smoke test failed unexpectedly: {result.Message}");
    }
}
