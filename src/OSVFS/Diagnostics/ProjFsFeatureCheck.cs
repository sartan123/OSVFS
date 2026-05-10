using Microsoft.Win32;
using System.Runtime.Versioning;

namespace OSVFS.Diagnostics;

/// <summary>
/// Verifies that the Windows ProjFS optional feature ("Client-ProjFS") is
/// installed and not disabled. We avoid the (managed-only) DISM API and just
/// look at the kernel-mode driver's service registration plus the user-mode
/// library — both are present iff the feature has been enabled and not since
/// uninstalled. This is the registry/file equivalent of
/// <c>Get-WindowsOptionalFeature -FeatureName Client-ProjFS</c>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ProjFsFeatureCheck : IDoctorCheck
{
    /// <summary>
    /// Service registry hive of the ProjFS minifilter driver (PrjFlt). Present
    /// when the optional feature has been turned on at least once.
    /// </summary>
    private const string PrjFltServiceKey = @"SYSTEM\CurrentControlSet\Services\PrjFlt";

    /// <summary>
    /// Disabled-service marker value of the <c>Start</c> column. A driver with
    /// <c>Start = 4</c> will not load on demand even if the feature is "installed".
    /// </summary>
    private const int ServiceStartDisabled = 4;

    /// <inheritdoc/>
    public string Name => "Windows ProjFS feature (Client-ProjFS)";

    /// <inheritdoc/>
    public Task<DoctorResult> RunAsync(CancellationToken ct) => Task.FromResult(Run());

    /// <summary>
    /// Inspects the registry and the system folder for the ProjFS driver and
    /// the user-mode helper library. Returns a single <see cref="DoctorResult"/>
    /// describing the most actionable failure first (e.g. "feature missing"
    /// outranks "feature installed but service disabled").
    /// </summary>
    private static DoctorResult Run()
    {
        using var serviceKey = Registry.LocalMachine.OpenSubKey(PrjFltServiceKey);
        if (serviceKey is null)
        {
            return new DoctorResult(
                "Windows ProjFS feature (Client-ProjFS)",
                DoctorCheckStatus.Fail,
                "ProjFS minifilter driver (PrjFlt) is not registered. Enable the feature with: " +
                "Enable-WindowsOptionalFeature -Online -FeatureName Client-ProjFS -NoRestart");
        }

        if (serviceKey.GetValue("Start") is int start && start == ServiceStartDisabled)
        {
            return new DoctorResult(
                "Windows ProjFS feature (Client-ProjFS)",
                DoctorCheckStatus.Fail,
                "PrjFlt service is registered but disabled (Start=4). Re-enable the optional " +
                "feature or set HKLM\\SYSTEM\\CurrentControlSet\\Services\\PrjFlt\\Start to 3.");
        }

        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var userModeLib = Path.Combine(system32, "ProjectedFSLib.dll");
        if (!File.Exists(userModeLib))
        {
            return new DoctorResult(
                "Windows ProjFS feature (Client-ProjFS)",
                DoctorCheckStatus.Fail,
                $"ProjFS user-mode library is missing ({userModeLib}). The feature is partially " +
                "installed; re-run Enable-WindowsOptionalFeature -Online -FeatureName Client-ProjFS.");
        }

        return new DoctorResult(
            "Windows ProjFS feature (Client-ProjFS)",
            DoctorCheckStatus.Pass,
            "PrjFlt driver registered and ProjectedFSLib.dll present.");
    }
}
