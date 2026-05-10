using System.Globalization;
using System.Text;
using OpenTelemetry.Metrics;

namespace OSVFS.Telemetry;

/// <summary>
/// Renders a <see cref="MetricSnapshot"/> as Prometheus text exposition
/// format (version 0.0.4). Hand-rolled rather than using
/// <c>OpenTelemetry.Exporter.Prometheus.HttpListener</c> because that
/// package is marked beta / dev-only and adds an AOT compatibility
/// surface this CLI does not need.
/// </summary>
internal static class PrometheusTextSerializer
{
    /// <summary>
    /// Content-Type header value Prometheus servers and scrapers expect
    /// for the v0.0.4 text exposition format.
    /// </summary>
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    /// <summary>
    /// Emits the full text-format payload for <paramref name="snapshot"/>.
    /// Empty input yields an empty string; the listener returns that
    /// verbatim and Prometheus treats it as "the target is up but had no
    /// samples to report".
    /// </summary>
    public static string Serialize(MetricSnapshot snapshot)
    {
        var sb = new StringBuilder(capacity: 1024);
        foreach (var family in snapshot.Families)
        {
            WriteFamily(sb, family);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Writes a single metric family (HELP + TYPE + per-point samples).
    /// Families with no measurements still emit the headers so dashboards
    /// can detect "is the instrument reachable?" before any traffic flows.
    /// </summary>
    private static void WriteFamily(StringBuilder sb, MetricFamilySnapshot family)
    {
        var promName = ComposePrometheusName(family);

        if (!string.IsNullOrEmpty(family.Description))
        {
            sb.Append("# HELP ").Append(promName).Append(' ')
              .Append(EscapeHelp(family.Description!)).Append('\n');
        }
        sb.Append("# TYPE ").Append(promName).Append(' ').Append(PromType(family.Type)).Append('\n');

        foreach (var point in family.Points)
        {
            switch (family.Type)
            {
                case MetricType.LongSum:
                case MetricType.LongSumNonMonotonic:
                    WriteSample(sb, promName, point.Tags, FormatLong(point.SumLong));
                    break;
                case MetricType.DoubleSum:
                case MetricType.DoubleSumNonMonotonic:
                    WriteSample(sb, promName, point.Tags, FormatDouble(point.SumDouble));
                    break;
                case MetricType.LongGauge:
                    WriteSample(sb, promName, point.Tags, FormatLong(point.GaugeLong));
                    break;
                case MetricType.DoubleGauge:
                    WriteSample(sb, promName, point.Tags, FormatDouble(point.GaugeDouble));
                    break;
                case MetricType.Histogram:
                    WriteHistogram(sb, promName, point);
                    break;
                default:
                    // Unknown / exponential histograms — skip the sample lines but keep
                    // the headers so the gap is observable.
                    break;
            }
        }

        sb.Append('\n');
    }

    /// <summary>
    /// Maps the OTel instrument name (and its unit) to a
    /// Prometheus-compliant identifier per the OTel→Prometheus 1.0
    /// mapping spec. Applies these transforms in order:
    /// <list type="number">
    ///   <item><description>Replace any character outside <c>[a-zA-Z0-9_:]</c> with <c>_</c>.</description></item>
    ///   <item><description>Strip a trailing <c>_total</c> so the unit suffix lands before it (counter idempotency).</description></item>
    ///   <item><description>Append the UCUM-derived unit suffix (<c>By</c> → <c>_bytes</c>, …) when the name does not already end with it.</description></item>
    ///   <item><description>Append <c>_total</c> for monotonic counters.</description></item>
    /// </list>
    /// </summary>
    private static string ComposePrometheusName(MetricFamilySnapshot family)
    {
        var sanitized = SanitizeName(family.Name);
        var isCounter = family.Type == MetricType.LongSum || family.Type == MetricType.DoubleSum;

        // Strip the trailing _total before applying the unit so a name like
        // "errors_total" with unit "{error}" stays "errors_total" and a name
        // like "already_seconds_total" with unit "s" doesn't acquire two suffixes.
        var hadTotal = false;
        if (EndsWithToken(sanitized, "total"))
        {
            sanitized = sanitized[..^"_total".Length];
            hadTotal = true;
        }

        var unitSuffix = MapUnit(family.Unit);
        if (!string.IsNullOrEmpty(unitSuffix) && !EndsWithToken(sanitized, unitSuffix!))
        {
            sanitized = sanitized + "_" + unitSuffix;
        }

        // Restore _total for counters (whether the input had it or not). For
        // non-counter families, only restore it when the input already had it,
        // matching the SDK's convention of leaving operator-chosen names alone.
        if (isCounter || hadTotal)
        {
            sanitized = sanitized + "_total";
        }

        return sanitized;
    }

    /// <summary>
    /// True when <paramref name="name"/> ends with <c>_<paramref name="suffix"/></c>
    /// or is exactly <paramref name="suffix"/>; used to keep idempotent transforms
    /// (already-suffixed inputs are not re-suffixed).
    /// </summary>
    private static bool EndsWithToken(string name, string suffix) =>
        name.Length == suffix.Length
            ? name.Equals(suffix, StringComparison.Ordinal)
            : name.Length > suffix.Length
                && name[name.Length - suffix.Length - 1] == '_'
                && name.AsSpan(name.Length - suffix.Length).SequenceEqual(suffix);

    /// <summary>
    /// Replaces every character outside the Prometheus identifier
    /// alphabet with <c>_</c>. Coalesces consecutive underscores so a
    /// dotted instrument name like <c>osvfs.s3.bytes_uploaded</c>
    /// renders as <c>osvfs_s3_bytes_uploaded</c> rather than
    /// <c>osvfs__s3__bytes_uploaded</c>.
    /// </summary>
    private static string SanitizeName(string name)
    {
        var sb = new StringBuilder(name.Length);
        var lastWasUnderscore = false;
        foreach (var c in name)
        {
            var ok = c == '_' || c == ':'
                || (c >= 'a' && c <= 'z')
                || (c >= 'A' && c <= 'Z')
                || (c >= '0' && c <= '9');
            if (ok)
            {
                if (c == '_')
                {
                    if (lastWasUnderscore) continue;
                    lastWasUnderscore = true;
                }
                else
                {
                    lastWasUnderscore = false;
                }
                sb.Append(c);
            }
            else
            {
                if (lastWasUnderscore) continue;
                sb.Append('_');
                lastWasUnderscore = true;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Returns the Prometheus name suffix for <paramref name="unit"/>
    /// per the OTel→Prometheus UCUM mapping table, or null when the
    /// unit is empty / annotation-only / unknown. Annotation units like
    /// <c>{error}</c> are dropped because they carry no scale.
    /// </summary>
    private static string? MapUnit(string? unit)
    {
        if (string.IsNullOrEmpty(unit)) return null;
        // Annotation-only units (e.g. "{error}", "{request}") collapse to nothing.
        if (unit.Length >= 2 && unit[0] == '{' && unit[^1] == '}') return null;
        return unit switch
        {
            "By" => "bytes",
            "kBy" => "kilobytes",
            "MBy" => "megabytes",
            "GBy" => "gigabytes",
            "ns" => "nanoseconds",
            "us" => "microseconds",
            "ms" => "milliseconds",
            "s" => "seconds",
            "min" => "minutes",
            "h" => "hours",
            "d" => "days",
            "%" => "ratio",
            _ => SanitizeName(unit),
        };
    }

    /// <summary>
    /// Maps the OTel <see cref="MetricType"/> to the Prometheus type
    /// keyword used on the <c># TYPE</c> line.
    /// </summary>
    private static string PromType(MetricType type) => type switch
    {
        MetricType.LongSum or MetricType.DoubleSum => "counter",
        MetricType.LongSumNonMonotonic or MetricType.DoubleSumNonMonotonic => "gauge",
        MetricType.LongGauge or MetricType.DoubleGauge => "gauge",
        MetricType.Histogram => "histogram",
        _ => "untyped",
    };

    /// <summary>
    /// Writes one <c>name{labels} value</c> sample line, including the
    /// trailing newline.
    /// </summary>
    private static void WriteSample(
        StringBuilder sb,
        string name,
        IReadOnlyList<KeyValuePair<string, string>> tags,
        string formattedValue)
    {
        sb.Append(name);
        WriteLabels(sb, tags);
        sb.Append(' ').Append(formattedValue).Append('\n');
    }

    /// <summary>
    /// Writes one histogram point as the standard <c>_bucket</c>,
    /// <c>_sum</c>, and <c>_count</c> series. Buckets are emitted with
    /// cumulative counts (Prometheus convention) and the implicit
    /// <c>+Inf</c> overflow bucket is appended last.
    /// </summary>
    private static void WriteHistogram(StringBuilder sb, string name, MetricPointSnapshot point)
    {
        var bounds = point.HistogramBucketBounds ?? [];
        var counts = point.HistogramBucketCounts ?? [];

        long cumulative = 0;
        for (var i = 0; i < bounds.Count; i++)
        {
            cumulative += counts[i];
            WriteBucketLine(sb, name, point.Tags, FormatDouble(bounds[i]), cumulative);
        }
        // Trailing overflow bucket. counts has Length = bounds.Count + 1 when the
        // exporter recorded an overflow sample; otherwise counts.Count == bounds.Count
        // and we still have to emit +Inf at the running total.
        if (counts.Count > bounds.Count)
        {
            cumulative += counts[bounds.Count];
        }
        WriteBucketLine(sb, name, point.Tags, "+Inf", cumulative);

        WriteSample(sb, name + "_sum", point.Tags, FormatDouble(point.SumDouble));
        WriteSample(sb, name + "_count", point.Tags, FormatLong(point.HistogramCount));
    }

    /// <summary>
    /// Writes one histogram <c>_bucket</c> sample with the supplied
    /// <c>le</c> label (already formatted) and cumulative count.
    /// </summary>
    private static void WriteBucketLine(
        StringBuilder sb,
        string name,
        IReadOnlyList<KeyValuePair<string, string>> tags,
        string leLabel,
        long cumulativeCount)
    {
        sb.Append(name).Append("_bucket");
        WriteLabels(sb, tags, extraKey: "le", extraValue: leLabel);
        sb.Append(' ').Append(FormatLong(cumulativeCount)).Append('\n');
    }

    /// <summary>
    /// Renders the label set, optionally appending an extra key/value
    /// pair (used by histogram buckets to inject <c>le="..."</c>).
    /// Emits no braces when both the tag set and the extra pair are
    /// empty so plain counters format as <c>name value</c>.
    /// </summary>
    private static void WriteLabels(
        StringBuilder sb,
        IReadOnlyList<KeyValuePair<string, string>> tags,
        string? extraKey = null,
        string? extraValue = null)
    {
        var hasExtra = extraKey is not null;
        if (tags.Count == 0 && !hasExtra) return;

        sb.Append('{');
        var first = true;
        foreach (var tag in tags)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(SanitizeLabelKey(tag.Key)).Append("=\"").Append(EscapeLabelValue(tag.Value)).Append('"');
        }
        if (hasExtra)
        {
            if (!first) sb.Append(',');
            sb.Append(extraKey).Append("=\"").Append(EscapeLabelValue(extraValue!)).Append('"');
        }
        sb.Append('}');
    }

    /// <summary>
    /// Same alphabet as <see cref="SanitizeName"/>; reused so a tag
    /// arriving with a dotted key (e.g. <c>http.status_code</c>) renders
    /// as a valid label name.
    /// </summary>
    private static string SanitizeLabelKey(string key) => SanitizeName(key);

    /// <summary>
    /// Escapes a label value per the v0.0.4 text format: backslash,
    /// double quote, and newline get a leading backslash.
    /// </summary>
    private static string EscapeLabelValue(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Escapes a HELP string per the v0.0.4 format: backslash and
    /// newline only (double quotes are not special in HELP text).
    /// </summary>
    private static string EscapeHelp(string help)
    {
        if (string.IsNullOrEmpty(help)) return string.Empty;
        var sb = new StringBuilder(help.Length);
        foreach (var c in help)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Renders an integer in invariant culture.
    /// </summary>
    private static string FormatLong(long value) =>
        value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Renders a double in invariant culture, preferring <c>G17</c> so
    /// the round-trip through <c>double.Parse</c> is exact, with
    /// <c>+Inf</c> / <c>-Inf</c> / <c>NaN</c> handled per the v0.0.4
    /// spec.
    /// </summary>
    private static string FormatDouble(double value)
    {
        if (double.IsPositiveInfinity(value)) return "+Inf";
        if (double.IsNegativeInfinity(value)) return "-Inf";
        if (double.IsNaN(value)) return "NaN";
        return value.ToString("R", CultureInfo.InvariantCulture);
    }
}
