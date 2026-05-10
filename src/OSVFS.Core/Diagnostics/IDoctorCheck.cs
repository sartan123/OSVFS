namespace OSVFS.Diagnostics;

/// <summary>
/// Single self-check executed by <see cref="OsvfsDoctor"/>. Implementations are
/// scoped to one concern (ProjFS feature presence, bucket access, credential
/// resolution, …) so they can be composed, swapped, and tested independently.
/// </summary>
internal interface IDoctorCheck
{
    /// <summary>
    /// Short label printed in the doctor table; should fit on one line.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Runs the check and returns its result. Implementations should never
    /// throw — failures are converted into <see cref="DoctorCheckStatus.Fail"/>
    /// results so the rest of the doctor run still completes.
    /// </summary>
    Task<DoctorResult> RunAsync(CancellationToken ct);
}
