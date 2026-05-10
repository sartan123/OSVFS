namespace OSVFS.Diagnostics;

/// <summary>
/// Sequentially runs a list of <see cref="IDoctorCheck"/> implementations and
/// returns their <see cref="DoctorResult"/>s. Catches every exception thrown
/// from a check so a single broken check cannot abort the whole diagnosis run.
/// </summary>
internal sealed class OsvfsDoctor
{
    private readonly IReadOnlyList<IDoctorCheck> checks;

    /// <summary>
    /// Constructs the orchestrator with a frozen ordered list of checks.
    /// </summary>
    public OsvfsDoctor(IReadOnlyList<IDoctorCheck> checks)
    {
        this.checks = checks;
    }

    /// <summary>
    /// Runs every check in order and returns the collected results. A check
    /// that throws is converted into a <see cref="DoctorCheckStatus.Fail"/>
    /// result whose detail captures the exception text for triage.
    /// </summary>
    public async Task<IReadOnlyList<DoctorResult>> RunAllAsync(CancellationToken ct)
    {
        var results = new List<DoctorResult>(checks.Count);
        foreach (var check in checks)
        {
            ct.ThrowIfCancellationRequested();
            DoctorResult result;
            try
            {
                result = await check.RunAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = new DoctorResult(
                    check.Name,
                    DoctorCheckStatus.Fail,
                    $"Check threw {ex.GetType().Name}: {ex.Message}",
                    ex.ToString());
            }
            results.Add(result);
        }
        return results;
    }

    /// <summary>
    /// Computes the process exit code from a set of results. Maps any
    /// <see cref="DoctorCheckStatus.Fail"/> to <c>2</c> (action required) and
    /// otherwise returns <c>0</c>; warnings and skips do not flip the code.
    /// </summary>
    public static int ToExitCode(IEnumerable<DoctorResult> results)
    {
        foreach (var r in results)
        {
            if (r.Status == DoctorCheckStatus.Fail) return 2;
        }
        return 0;
    }
}
