using Microsoft.Windows.ProjFS;
using System.Runtime.Versioning;

namespace OSVFS.Diagnostics;

/// <summary>
/// End-to-end smoke test for the local ProjFS stack: creates a throwaway
/// directory, marks it as a virtualization root, opens a
/// <see cref="VirtualizationInstance"/>, calls <c>StartVirtualizing</c>, and
/// tears everything down again. Catches the class of failures the registry
/// check in <see cref="ProjFsFeatureCheck"/> cannot see — service stopped,
/// Defender / EDR rejecting the minifilter, ProjectedFSLib mismatch.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ProjFsStartCheck : IDoctorCheck
{
    /// <inheritdoc/>
    public string Name => "ProjFS StartVirtualizing smoke test";

    /// <inheritdoc/>
    public Task<DoctorResult> RunAsync(CancellationToken ct) => Task.FromResult(Run());

    /// <summary>
    /// Spins up and tears down a virtualization instance against a freshly
    /// created temp directory. The directory and everything inside it are
    /// always cleaned up, even on failure.
    /// </summary>
    private static DoctorResult Run()
    {
        var tempRoot = Path.Combine(
            Path.GetTempPath(), "osvfs-doctor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        VirtualizationInstance? instance = null;
        var started = false;
        try
        {
            var markHr = VirtualizationInstance.MarkDirectoryAsVirtualizationRoot(
                tempRoot, Guid.NewGuid());
            if (markHr is not (HResult.Ok or HResult.VirtualizationInvalidOp or HResult.ReparsePointEncountered))
            {
                return new DoctorResult(
                    "ProjFS StartVirtualizing smoke test",
                    DoctorCheckStatus.Fail,
                    $"MarkDirectoryAsVirtualizationRoot failed for {tempRoot}: {markHr}.");
            }

            instance = new VirtualizationInstance(
                tempRoot,
                poolThreadCount: 0,
                concurrentThreadCount: 0,
                enableNegativePathCache: false,
                notificationMappings: []);

            var startHr = instance.StartVirtualizing(new NullCallbacks());
            if (startHr != HResult.Ok)
            {
                return new DoctorResult(
                    "ProjFS StartVirtualizing smoke test",
                    DoctorCheckStatus.Fail,
                    $"StartVirtualizing returned {startHr}. The PrjFlt service may be stopped or " +
                    "blocked by EDR / antivirus. Try: sc query PrjFlt — and check Event Viewer → " +
                    "System for PrjFlt entries.");
            }
            started = true;
            return new DoctorResult(
                "ProjFS StartVirtualizing smoke test",
                DoctorCheckStatus.Pass,
                "MarkDirectoryAsVirtualizationRoot + StartVirtualizing both succeeded.");
        }
        catch (Exception ex)
        {
            return new DoctorResult(
                "ProjFS StartVirtualizing smoke test",
                DoctorCheckStatus.Fail,
                $"ProjFS smoke test threw {ex.GetType().Name}: {ex.Message}",
                ex.ToString());
        }
        finally
        {
            if (instance is not null)
            {
                if (started)
                {
                    try { instance.StopVirtualizing(); }
                    catch { /* nothing actionable; we're tearing down. */ }
                }
            }
            // The temp folder carries a reparse point from MarkDirectoryAsVirtualizationRoot
            // even after StopVirtualizing; deleting recursively still works because the
            // virt instance is no longer running.
            try { Directory.Delete(tempRoot, recursive: true); }
            catch { /* best-effort cleanup. */ }
        }
    }

    /// <summary>
    /// Stub <see cref="IRequiredCallbacks"/> used purely so
    /// <c>StartVirtualizing</c> has something to attach. We never actually
    /// enumerate or hydrate anything against the temp directory, but ProjFS
    /// requires a non-null callback target.
    /// </summary>
    private sealed class NullCallbacks : IRequiredCallbacks
    {
        public HResult StartDirectoryEnumerationCallback(
            int commandId, Guid enumerationId, string relativePath,
            uint triggeringProcessId, string triggeringProcessImageFileName) => HResult.Ok;

        public HResult EndDirectoryEnumerationCallback(Guid enumerationId) => HResult.Ok;

        public HResult GetDirectoryEnumerationCallback(
            int commandId, Guid enumerationId, string filterFileName, bool restartScan,
            IDirectoryEnumerationResults result) => HResult.Ok;

        public HResult GetPlaceholderInfoCallback(
            int commandId, string relativePath, uint triggeringProcessId,
            string triggeringProcessImageFileName) => HResult.FileNotFound;

        public HResult GetFileDataCallback(
            int commandId, string relativePath, ulong byteOffset, uint length,
            Guid dataStreamId, byte[] contentId, byte[] providerId,
            uint triggeringProcessId, string triggeringProcessImageFileName) => HResult.FileNotFound;
    }
}
