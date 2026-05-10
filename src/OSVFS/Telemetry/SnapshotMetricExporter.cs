using System.Collections.Generic;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace OSVFS.Telemetry;

/// <summary>
/// Pull-style OTel metric exporter that captures the most recent
/// <see cref="Batch{T}"/> as an immutable <see cref="MetricSnapshot"/>.
/// Wired into the <see cref="MeterProvider"/> through a
/// <see cref="BaseExportingMetricReader"/>; <see cref="OsvfsMetricsListener"/>
/// triggers <see cref="MetricReader.Collect"/> on each <c>/metrics</c>
/// scrape so the snapshot is rebuilt synchronously from the SDK's
/// internal aggregator.
/// </summary>
internal sealed class SnapshotMetricExporter : BaseExporter<Metric>
{
    /// <summary>
    /// Latest captured snapshot. <c>volatile</c> + reference write is
    /// sufficient because we only swap the reference; readers access
    /// it via the <see cref="GetSnapshot"/> accessor.
    /// </summary>
    private volatile MetricSnapshot? snapshot;

    /// <summary>
    /// Returns the most recently captured snapshot, or an empty snapshot
    /// when no collection has run yet (e.g. scrape arrives before the
    /// first <see cref="Export"/> call). The returned object is
    /// immutable; callers may iterate it without locking.
    /// </summary>
    public MetricSnapshot GetSnapshot() => snapshot ?? new MetricSnapshot();

    /// <summary>
    /// Materializes <paramref name="batch"/> into a new
    /// <see cref="MetricSnapshot"/> and atomically swaps it into
    /// <see cref="snapshot"/>. Always returns
    /// <see cref="ExportResult.Success"/>: a failure here would abort the
    /// SDK's collection loop, but a missed scrape simply replays the
    /// previous snapshot which is preferable to losing the OTLP push too.
    /// </summary>
    public override ExportResult Export(in Batch<Metric> batch)
    {
        var families = new List<MetricFamilySnapshot>();
        foreach (var metric in batch)
        {
            families.Add(BuildFamily(metric));
        }
        snapshot = new MetricSnapshot { Families = families };
        return ExportResult.Success;
    }

    /// <summary>
    /// Deep-copies a single <see cref="Metric"/> and its point set into
    /// the snapshot model. Bucket arrays are materialized eagerly because
    /// <see cref="HistogramBuckets"/> references the SDK's pooled state.
    /// </summary>
    private static MetricFamilySnapshot BuildFamily(Metric metric)
    {
        var points = new List<MetricPointSnapshot>();
        foreach (ref readonly var point in metric.GetMetricPoints())
        {
            points.Add(BuildPoint(metric.MetricType, in point));
        }
        return new MetricFamilySnapshot
        {
            Name = metric.Name,
            Description = metric.Description,
            Unit = metric.Unit,
            Type = metric.MetricType,
            Points = points,
        };
    }

    /// <summary>
    /// Snapshots a single <see cref="MetricPoint"/>. Copies tags and any
    /// histogram bucket boundaries / counts into independent arrays so
    /// the data outlives <paramref name="point"/>.
    /// </summary>
    private static MetricPointSnapshot BuildPoint(MetricType type, in MetricPoint point)
    {
        var tags = CopyTags(point.Tags);

        if (type == MetricType.Histogram)
        {
            var bounds = new List<double>();
            var counts = new List<long>();
            foreach (var bucket in point.GetHistogramBuckets())
            {
                if (!double.IsPositiveInfinity(bucket.ExplicitBound))
                {
                    bounds.Add(bucket.ExplicitBound);
                }
                counts.Add(bucket.BucketCount);
            }
            return new MetricPointSnapshot
            {
                Tags = tags,
                SumDouble = point.GetHistogramSum(),
                HistogramCount = point.GetHistogramCount(),
                HistogramBucketBounds = bounds,
                HistogramBucketCounts = counts,
            };
        }

        return type switch
        {
            MetricType.LongSum or MetricType.LongSumNonMonotonic => new MetricPointSnapshot
            {
                Tags = tags,
                SumLong = point.GetSumLong(),
            },
            MetricType.DoubleSum or MetricType.DoubleSumNonMonotonic => new MetricPointSnapshot
            {
                Tags = tags,
                SumDouble = point.GetSumDouble(),
            },
            MetricType.LongGauge => new MetricPointSnapshot
            {
                Tags = tags,
                GaugeLong = point.GetGaugeLastValueLong(),
            },
            MetricType.DoubleGauge => new MetricPointSnapshot
            {
                Tags = tags,
                GaugeDouble = point.GetGaugeLastValueDouble(),
            },
            // Exponential histograms and any future kinds we don't
            // model fall through with empty values; the serializer
            // emits the family header only so the operator can spot
            // the gap and we don't crash on first scrape.
            _ => new MetricPointSnapshot { Tags = tags },
        };
    }

    /// <summary>
    /// Materializes the tag enumeration into a plain list of string
    /// pairs. Keys are kept as-is (Prometheus accepts the same
    /// identifier set we do for instruments); values are stringified
    /// because Prometheus is text-only.
    /// </summary>
    private static List<KeyValuePair<string, string>> CopyTags(ReadOnlyTagCollection tags)
    {
        if (tags.Count == 0) return [];
        var list = new List<KeyValuePair<string, string>>(tags.Count);
        foreach (var tag in tags)
        {
            list.Add(new KeyValuePair<string, string>(
                tag.Key,
                tag.Value?.ToString() ?? string.Empty));
        }
        return list;
    }
}
