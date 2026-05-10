namespace OSVFS.Diagnostics;

/// <summary>
/// Single check outcome produced by an <see cref="IDoctorCheck"/>. Captures the
/// human-readable name (used as the row label), the status glyph, a one-line
/// summary, and an optional multi-line detail block (typically a stack-trace or
/// remediation hint) the renderer prints under the row.
/// </summary>
internal sealed record DoctorResult(
    string Name,
    DoctorCheckStatus Status,
    string Message,
    string? Detail = null);
