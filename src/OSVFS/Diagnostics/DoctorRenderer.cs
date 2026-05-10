namespace OSVFS.Diagnostics;

/// <summary>
/// Prints a list of <see cref="DoctorResult"/>s to the console using a small
/// ANSI escape-code palette. We deliberately do not pull in Spectre.Console:
/// the table is one column of <c>name : message</c> rows, the output is small
/// enough that a hand-rolled formatter beats the dependency cost (Spectre adds
/// a megabyte+ of native-AOT-trimmed code), and ANSI is universally rendered
/// by Windows Terminal, VS Code's terminal, and Cmder. <c>NO_COLOR</c> and
/// non-redirected output disable colors automatically.
/// </summary>
internal static class DoctorRenderer
{
    private const string AnsiReset = "\x1b[0m";
    private const string AnsiGreen = "\x1b[32m";
    private const string AnsiYellow = "\x1b[33m";
    private const string AnsiRed = "\x1b[31m";
    private const string AnsiCyan = "\x1b[36m";
    private const string AnsiBold = "\x1b[1m";

    /// <summary>
    /// Writes a heading line and one row per result to <paramref name="writer"/>.
    /// Colors are skipped automatically when stdout is redirected or
    /// <c>NO_COLOR</c> is set so log shippers and CI scrapers see clean text.
    /// </summary>
    public static void Render(IReadOnlyList<DoctorResult> results, TextWriter writer)
    {
        var color = ShouldUseColor(writer);
        Heading(writer, color);
        foreach (var r in results)
        {
            WriteRow(writer, r, color);
        }
        writer.WriteLine();
        WriteSummary(writer, results, color);
    }

    /// <summary>
    /// Prints the header line. Kept on its own line so the renderer's tests
    /// can match it without depending on layout details.
    /// </summary>
    private static void Heading(TextWriter writer, bool color)
    {
        if (color) writer.Write(AnsiBold + AnsiCyan);
        writer.Write("OSVFS doctor — environment self-check");
        if (color) writer.Write(AnsiReset);
        writer.WriteLine();
        writer.WriteLine(new string('-', 60));
    }

    /// <summary>
    /// Writes a single check row in the form <c>[glyph] Name — Message</c>,
    /// followed by an indented detail block when present.
    /// </summary>
    private static void WriteRow(TextWriter writer, DoctorResult result, bool color)
    {
        var (glyph, hue) = GlyphFor(result.Status);
        if (color) writer.Write(hue);
        writer.Write(glyph);
        if (color) writer.Write(AnsiReset);
        writer.Write(' ');
        writer.Write(result.Name);
        writer.Write(": ");
        writer.WriteLine(result.Message);

        if (!string.IsNullOrEmpty(result.Detail))
        {
            using var reader = new StringReader(result.Detail);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                writer.Write("    ");
                writer.WriteLine(line);
            }
        }
    }

    /// <summary>
    /// Glyph + ANSI color pairing used for each status. Returning a tuple keeps
    /// the renderer free of nested <c>switch</c> / formatting branches.
    /// </summary>
    private static (string Glyph, string AnsiColor) GlyphFor(DoctorCheckStatus status) => status switch
    {
        DoctorCheckStatus.Pass => ("[OK]", AnsiGreen),
        DoctorCheckStatus.Warn => ("[!!]", AnsiYellow),
        DoctorCheckStatus.Fail => ("[XX]", AnsiRed),
        DoctorCheckStatus.Skipped => ("[--]", AnsiYellow),
        _ => ("[??]", AnsiYellow),
    };

    /// <summary>
    /// Tail line that summarizes the run. Mirrors the per-row glyphs so a
    /// truncated terminal still gives the operator the headline result.
    /// </summary>
    private static void WriteSummary(TextWriter writer, IReadOnlyList<DoctorResult> results, bool color)
    {
        int pass = 0, warn = 0, fail = 0, skipped = 0;
        foreach (var r in results)
        {
            switch (r.Status)
            {
                case DoctorCheckStatus.Pass: pass++; break;
                case DoctorCheckStatus.Warn: warn++; break;
                case DoctorCheckStatus.Fail: fail++; break;
                case DoctorCheckStatus.Skipped: skipped++; break;
            }
        }

        if (fail > 0)
        {
            if (color) writer.Write(AnsiRed + AnsiBold);
            writer.Write($"FAILED — {fail} check(s) need action ({pass} ok, {warn} warn, {skipped} skipped).");
        }
        else if (warn > 0 || skipped > 0)
        {
            if (color) writer.Write(AnsiYellow + AnsiBold);
            writer.Write($"OK with notes — {pass} ok, {warn} warn, {skipped} skipped.");
        }
        else
        {
            if (color) writer.Write(AnsiGreen + AnsiBold);
            writer.Write($"All {pass} check(s) passed.");
        }
        if (color) writer.Write(AnsiReset);
        writer.WriteLine();
    }

    /// <summary>
    /// Returns true when the writer is the live console (not redirected) and
    /// the user has not opted out via <c>NO_COLOR</c>. Mirrors the convention
    /// the rest of the .NET ecosystem (dotnet CLI, Spectre.Console) uses.
    /// </summary>
    private static bool ShouldUseColor(TextWriter writer)
    {
        if (Environment.GetEnvironmentVariable("NO_COLOR") is { Length: > 0 }) return false;
        if (writer == Console.Out && Console.IsOutputRedirected) return false;
        if (writer == Console.Error && Console.IsErrorRedirected) return false;
        return true;
    }
}
