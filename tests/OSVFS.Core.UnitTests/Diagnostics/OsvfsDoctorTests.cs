using OSVFS.Diagnostics;
using Xunit;

namespace OSVFS.Core.UnitTests.Diagnostics;

/// <summary>
/// Drives the orchestrator with stub <see cref="IDoctorCheck"/> implementations so
/// the ordering, exception-to-fail conversion, and exit-code mapping rules are
/// exercised independently of any real check.
/// </summary>
public sealed class OsvfsDoctorTests
{
    [Fact]
    public async Task RunAllAsync_invokes_every_check_in_declaration_order()
    {
        var calls = new List<string>();
        var doctor = new OsvfsDoctor(
        [
            new TraceCheck("a", calls, DoctorCheckStatus.Pass),
            new TraceCheck("b", calls, DoctorCheckStatus.Pass),
            new TraceCheck("c", calls, DoctorCheckStatus.Pass),
        ]);

        var results = await doctor.RunAllAsync(CancellationToken.None);

        Assert.Equal(["a", "b", "c"], calls);
        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Equal(DoctorCheckStatus.Pass, r.Status));
    }

    [Fact]
    public async Task RunAllAsync_converts_thrown_exception_to_failure_result()
    {
        var doctor = new OsvfsDoctor(
        [
            new ThrowingCheck("boom", new InvalidOperationException("nope")),
            new TraceCheck("after", [], DoctorCheckStatus.Pass),
        ]);

        var results = await doctor.RunAllAsync(CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal(DoctorCheckStatus.Fail, results[0].Status);
        Assert.Contains("InvalidOperationException", results[0].Message);
        Assert.Contains("nope", results[0].Message);
        Assert.NotNull(results[0].Detail);
        Assert.Equal(DoctorCheckStatus.Pass, results[1].Status);
    }

    [Fact]
    public async Task RunAllAsync_propagates_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var doctor = new OsvfsDoctor([new TraceCheck("a", [], DoctorCheckStatus.Pass)]);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => doctor.RunAllAsync(cts.Token));
    }

    [Fact]
    public void ToExitCode_returns_zero_for_all_pass()
    {
        Assert.Equal(0, OsvfsDoctor.ToExitCode(new[]
        {
            new DoctorResult("a", DoctorCheckStatus.Pass, ""),
            new DoctorResult("b", DoctorCheckStatus.Pass, ""),
        }));
    }

    [Fact]
    public void ToExitCode_returns_zero_when_only_warnings_or_skips_present()
    {
        Assert.Equal(0, OsvfsDoctor.ToExitCode(new[]
        {
            new DoctorResult("a", DoctorCheckStatus.Pass, ""),
            new DoctorResult("b", DoctorCheckStatus.Warn, ""),
            new DoctorResult("c", DoctorCheckStatus.Skipped, ""),
        }));
    }

    [Fact]
    public void ToExitCode_returns_two_when_any_failure_is_present()
    {
        Assert.Equal(2, OsvfsDoctor.ToExitCode(new[]
        {
            new DoctorResult("a", DoctorCheckStatus.Pass, ""),
            new DoctorResult("b", DoctorCheckStatus.Fail, ""),
        }));
    }

    [Fact]
    public void ToExitCode_returns_zero_for_empty_input()
    {
        Assert.Equal(0, OsvfsDoctor.ToExitCode([]));
    }

    private sealed class TraceCheck : IDoctorCheck
    {
        private readonly List<string> log;
        private readonly DoctorCheckStatus status;

        public string Name { get; }

        public TraceCheck(string name, List<string> log, DoctorCheckStatus status)
        {
            Name = name;
            this.log = log;
            this.status = status;
        }

        public Task<DoctorResult> RunAsync(CancellationToken ct)
        {
            log.Add(Name);
            return Task.FromResult(new DoctorResult(Name, status, "ok"));
        }
    }

    private sealed class ThrowingCheck : IDoctorCheck
    {
        private readonly Exception ex;

        public string Name { get; }

        public ThrowingCheck(string name, Exception ex)
        {
            Name = name;
            this.ex = ex;
        }

        public Task<DoctorResult> RunAsync(CancellationToken ct) => throw ex;
    }
}
