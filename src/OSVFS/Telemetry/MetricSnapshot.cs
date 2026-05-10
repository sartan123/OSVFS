using System.Collections.Generic;
using OpenTelemetry.Metrics;

namespace OSVFS.Telemetry;

/// <summary>
/// Immutable, deep-copied view of one OTel metric collection cycle. Built
/// inside <see cref="SnapshotMetricExporter.Export"/> while the
/// <see cref="Batch{T}"/> reference is still alive, then handed off to
/// <see cref="PrometheusTextSerializer"/> at scrape time. We snapshot
/// rather than re-iterate the batch because <see cref="MetricPoint"/>
/// values are tied to the SDK's internal aggregator and are unsafe to
/// hold past the Export call.
/// </summary>
internal sealed class MetricSnapshot
{
    /// <summary>
    /// One entry per <see cref="Metric"/> in the exported batch. Order
    /// matches the SDK's emission order, which keeps related families
    /// (e.g. <c>osvfs.s3.duration</c> and <c>osvfs.s3.errors_total</c>)
    /// adjacent in the rendered output.
    /// </summary>
    public IReadOnlyList<MetricFamilySnapshot> Families { get; init; } = [];
}

/// <summary>
/// Per-metric snapshot: the family-wide attributes (name / description /
/// unit / type) plus one <see cref="MetricPointSnapshot"/> per attribute
/// combination observed during the cycle.
/// </summary>
internal sealed class MetricFamilySnapshot
{
    /// <summary>
    /// Original instrument name (e.g. <c>osvfs.s3.bytes_uploaded</c>).
    /// Sanitized to a Prometheus-safe identifier inside the serializer.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Human-readable description copied from
    /// <see cref="Metric.Description"/>. Rendered into the
    /// <c># HELP</c> line; empty / null suppresses the line.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// UCUM unit copied from <see cref="Metric.Unit"/>. The serializer
    /// folds it into the Prometheus name suffix per the OTel→Prom
    /// mapping (e.g. <c>By</c> → <c>_bytes</c>).
    /// </summary>
    public string? Unit { get; init; }

    /// <summary>
    /// OTel metric kind. Drives both the <c># TYPE</c> line and the
    /// per-point sample shape (counter sample, histogram bucket lines, …).
    /// </summary>
    public required MetricType Type { get; init; }

    /// <summary>
    /// One entry per attribute combination seen during the collection
    /// cycle. Empty when no measurements were recorded for the family.
    /// </summary>
    public IReadOnlyList<MetricPointSnapshot> Points { get; init; } = [];
}

/// <summary>
/// Per-attribute-combination snapshot. Populates only the fields that
/// match the parent family's <see cref="MetricFamilySnapshot.Type"/> —
/// e.g. histogram buckets are null for counter families.
/// </summary>
internal sealed class MetricPointSnapshot
{
    /// <summary>
    /// Attribute set as a flat list of key/value pairs. Stored as the
    /// flat array ProjFS-style serializers expect; the Prometheus
    /// renderer iterates in order so adjacent labels stay grouped.
    /// </summary>
    public required IReadOnlyList<KeyValuePair<string, string>> Tags { get; init; }

    /// <summary>
    /// Counter / non-monotonic-sum value when the family is a long-typed
    /// sum. Ignored for double sums and histograms.
    /// </summary>
    public long SumLong { get; init; }

    /// <summary>
    /// Counter / non-monotonic-sum value when the family is a
    /// double-typed sum, and total observed value when the family is a
    /// histogram (<c>_sum</c> series).
    /// </summary>
    public double SumDouble { get; init; }

    /// <summary>
    /// Gauge value when the family is long-typed. Ignored otherwise.
    /// </summary>
    public long GaugeLong { get; init; }

    /// <summary>
    /// Gauge value when the family is double-typed. Ignored otherwise.
    /// </summary>
    public double GaugeDouble { get; init; }

    /// <summary>
    /// Total observation count for histogram families (<c>_count</c>
    /// series). Zero for non-histogram families.
    /// </summary>
    public long HistogramCount { get; init; }

    /// <summary>
    /// Inclusive upper bounds of the histogram buckets, one per entry
    /// in <see cref="HistogramBucketCounts"/>. The implicit overflow
    /// bucket (<c>+Inf</c>) is appended by the serializer. Null for
    /// non-histogram families.
    /// </summary>
    public IReadOnlyList<double>? HistogramBucketBounds { get; init; }

    /// <summary>
    /// Cumulative bucket counts. Length is <see cref="HistogramBucketBounds"/>'
    /// length plus one (the trailing entry is the overflow bucket count).
    /// Null for non-histogram families.
    /// </summary>
    public IReadOnlyList<long>? HistogramBucketCounts { get; init; }
}
