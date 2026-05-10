namespace OSVFS.Diagnostics;

/// <summary>
/// Outcome bucket for a single <see cref="IDoctorCheck"/> run. Maps directly
/// onto the ✓/✗/⚠ glyphs the renderer prints and onto the doctor's overall
/// exit code (any <see cref="Fail"/> drives a non-zero exit).
/// </summary>
internal enum DoctorCheckStatus
{
    /// <summary>
    /// The check completed successfully and the inspected resource is in the desired state.
    /// </summary>
    Pass,

    /// <summary>
    /// The check completed but surfaced a non-blocking concern (e.g. versioning
    /// suspended) the operator should review.
    /// </summary>
    Warn,

    /// <summary>
    /// The check failed and the operator must take action before OSVFS can mount.
    /// </summary>
    Fail,

    /// <summary>
    /// The check could not run because a prerequisite (option, parameter, file)
    /// was absent. Treated like a warning for exit-code purposes.
    /// </summary>
    Skipped,
}
