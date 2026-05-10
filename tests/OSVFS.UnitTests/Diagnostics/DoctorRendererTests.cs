using OSVFS.Diagnostics;
using Xunit;

namespace OSVFS.UnitTests.Diagnostics;

/// <summary>
/// Verifies the layout decisions <see cref="DoctorRenderer"/> makes when
/// rendering a result list to a <see cref="StringWriter"/>: status glyphs,
/// detail-block indentation, and the headline summary's pass/warn/fail wording.
/// </summary>
public sealed class DoctorRendererTests
{
    public DoctorRendererTests()
    {
        // Force colors off so the test asserts on plain text rather than the ANSI
        // wrapper. The renderer also disables colors when stdout is redirected,
        // which a StringWriter does not signal.
        Environment.SetEnvironmentVariable("NO_COLOR", "1");
    }

    [Fact]
    public void Renders_pass_warn_fail_glyphs_and_detail_indentation()
    {
        var writer = new StringWriter();
        DoctorRenderer.Render(
        [
            new DoctorResult("alpha", DoctorCheckStatus.Pass, "ok"),
            new DoctorResult("beta", DoctorCheckStatus.Warn, "look at me"),
            new DoctorResult("gamma", DoctorCheckStatus.Fail, "broken", "line1\nline2"),
            new DoctorResult("delta", DoctorCheckStatus.Skipped, "not configured"),
        ], writer);

        var output = writer.ToString();
        Assert.Contains("[OK] alpha: ok", output);
        Assert.Contains("[!!] beta: look at me", output);
        Assert.Contains("[XX] gamma: broken", output);
        Assert.Contains("    line1", output);
        Assert.Contains("    line2", output);
        Assert.Contains("[--] delta: not configured", output);
    }

    [Fact]
    public void Summary_announces_failure_when_any_check_failed()
    {
        var writer = new StringWriter();
        DoctorRenderer.Render(
        [
            new DoctorResult("a", DoctorCheckStatus.Pass, ""),
            new DoctorResult("b", DoctorCheckStatus.Fail, ""),
        ], writer);

        Assert.Contains("FAILED", writer.ToString());
    }

    [Fact]
    public void Summary_announces_warnings_when_no_failures()
    {
        var writer = new StringWriter();
        DoctorRenderer.Render(
        [
            new DoctorResult("a", DoctorCheckStatus.Pass, ""),
            new DoctorResult("b", DoctorCheckStatus.Warn, ""),
        ], writer);

        Assert.Contains("OK with notes", writer.ToString());
    }

    [Fact]
    public void Summary_announces_clean_pass_when_everything_is_ok()
    {
        var writer = new StringWriter();
        DoctorRenderer.Render(
        [
            new DoctorResult("a", DoctorCheckStatus.Pass, ""),
            new DoctorResult("b", DoctorCheckStatus.Pass, ""),
        ], writer);

        Assert.Contains("All 2 check(s) passed.", writer.ToString());
    }
}
