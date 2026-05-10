using OpenTelemetry.Metrics;
using OSVFS.Telemetry;
using Xunit;

namespace OSVFS.UnitTests.Telemetry;

/// <summary>
/// Unit coverage for the Prometheus text exposition serializer. Builds
/// snapshots directly so the tests do not require an OTel
/// <see cref="MeterProvider"/> to drive an end-to-end pipeline.
/// </summary>
public class PrometheusTextSerializerTests
{
    [Fact]
    public void Serialize_empty_snapshot_returns_empty_string()
    {
        var output = PrometheusTextSerializer.Serialize(new MetricSnapshot());
        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public void Serialize_long_counter_appends_total_suffix_and_unit()
    {
        var snapshot = new MetricSnapshot
        {
            Families =
            [
                new MetricFamilySnapshot
                {
                    Name = "osvfs.s3.bytes_uploaded",
                    Description = "Total bytes successfully uploaded to the S3 backend.",
                    Unit = "By",
                    Type = MetricType.LongSum,
                    Points =
                    [
                        new MetricPointSnapshot { Tags = [], SumLong = 12_345 },
                    ],
                },
            ],
        };

        var output = PrometheusTextSerializer.Serialize(snapshot);

        Assert.Contains("# HELP osvfs_s3_bytes_uploaded_bytes_total Total bytes successfully uploaded to the S3 backend.\n", output, StringComparison.Ordinal);
        Assert.Contains("# TYPE osvfs_s3_bytes_uploaded_bytes_total counter\n", output, StringComparison.Ordinal);
        Assert.Contains("osvfs_s3_bytes_uploaded_bytes_total 12345\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_counter_partitioned_by_tag_emits_one_sample_per_tagset()
    {
        var snapshot = new MetricSnapshot
        {
            Families =
            [
                new MetricFamilySnapshot
                {
                    Name = "osvfs.s3.errors",
                    Description = "S3 errors",
                    Unit = "{error}",
                    Type = MetricType.LongSum,
                    Points =
                    [
                        new MetricPointSnapshot
                        {
                            Tags = [new("operation", "Get")], SumLong = 3,
                        },
                        new MetricPointSnapshot
                        {
                            Tags = [new("operation", "Put")], SumLong = 1,
                        },
                    ],
                },
            ],
        };

        var output = PrometheusTextSerializer.Serialize(snapshot);

        // Annotation unit "{error}" collapses to nothing; counter still gets _total.
        Assert.Contains("# TYPE osvfs_s3_errors_total counter\n", output, StringComparison.Ordinal);
        Assert.Contains("osvfs_s3_errors_total{operation=\"Get\"} 3\n", output, StringComparison.Ordinal);
        Assert.Contains("osvfs_s3_errors_total{operation=\"Put\"} 1\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_histogram_emits_cumulative_buckets_sum_and_count()
    {
        var snapshot = new MetricSnapshot
        {
            Families =
            [
                new MetricFamilySnapshot
                {
                    Name = "osvfs.s3.duration",
                    Description = "Latency",
                    Unit = "ms",
                    Type = MetricType.Histogram,
                    Points =
                    [
                        new MetricPointSnapshot
                        {
                            Tags = [new("operation", "Get")],
                            HistogramBucketBounds = [5d, 10d, 25d],
                            // Per-bucket (delta) counts: 2 in <=5, 1 in <=10, 0 in <=25, 1 in +Inf.
                            HistogramBucketCounts = [2, 1, 0, 1],
                            HistogramCount = 4,
                            SumDouble = 47.5,
                        },
                    ],
                },
            ],
        };

        var output = PrometheusTextSerializer.Serialize(snapshot);

        Assert.Contains("# TYPE osvfs_s3_duration_milliseconds histogram\n", output, StringComparison.Ordinal);
        // Cumulative bucket counts: 2, 3, 3, 4
        Assert.Contains("osvfs_s3_duration_milliseconds_bucket{operation=\"Get\",le=\"5\"} 2\n", output, StringComparison.Ordinal);
        Assert.Contains("osvfs_s3_duration_milliseconds_bucket{operation=\"Get\",le=\"10\"} 3\n", output, StringComparison.Ordinal);
        Assert.Contains("osvfs_s3_duration_milliseconds_bucket{operation=\"Get\",le=\"25\"} 3\n", output, StringComparison.Ordinal);
        Assert.Contains("osvfs_s3_duration_milliseconds_bucket{operation=\"Get\",le=\"+Inf\"} 4\n", output, StringComparison.Ordinal);
        Assert.Contains("osvfs_s3_duration_milliseconds_sum{operation=\"Get\"} 47.5\n", output, StringComparison.Ordinal);
        Assert.Contains("osvfs_s3_duration_milliseconds_count{operation=\"Get\"} 4\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_long_gauge_writes_gauge_type()
    {
        var snapshot = new MetricSnapshot
        {
            Families =
            [
                new MetricFamilySnapshot
                {
                    Name = "queue.depth",
                    Description = "depth",
                    Unit = null,
                    Type = MetricType.LongGauge,
                    Points = [new MetricPointSnapshot { Tags = [], GaugeLong = 42 }],
                },
            ],
        };

        var output = PrometheusTextSerializer.Serialize(snapshot);

        Assert.Contains("# TYPE queue_depth gauge\n", output, StringComparison.Ordinal);
        Assert.Contains("queue_depth 42\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_label_values_escape_quote_backslash_and_newline()
    {
        var snapshot = new MetricSnapshot
        {
            Families =
            [
                new MetricFamilySnapshot
                {
                    Name = "x.count",
                    Type = MetricType.LongSum,
                    Points =
                    [
                        new MetricPointSnapshot
                        {
                            Tags = [new("op", "a\"b\\c\nd")],
                            SumLong = 1,
                        },
                    ],
                },
            ],
        };

        var output = PrometheusTextSerializer.Serialize(snapshot);

        Assert.Contains("x_count_total{op=\"a\\\"b\\\\c\\nd\"} 1\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_idempotent_when_name_already_has_total_or_unit_suffix()
    {
        var snapshot = new MetricSnapshot
        {
            Families =
            [
                new MetricFamilySnapshot
                {
                    Name = "already_seconds_total",
                    Unit = "s",
                    Type = MetricType.LongSum,
                    Points = [new MetricPointSnapshot { Tags = [], SumLong = 7 }],
                },
            ],
        };

        var output = PrometheusTextSerializer.Serialize(snapshot);

        // Must not produce already_seconds_total_seconds_total
        Assert.Contains("# TYPE already_seconds_total counter\n", output, StringComparison.Ordinal);
        Assert.Contains("already_seconds_total 7\n", output, StringComparison.Ordinal);
        Assert.DoesNotContain("already_seconds_total_seconds_total", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialize_dotted_name_replaces_dots_with_underscores()
    {
        var snapshot = new MetricSnapshot
        {
            Families =
            [
                new MetricFamilySnapshot
                {
                    Name = "osvfs.projfs.errors_total",
                    Unit = "{error}",
                    Type = MetricType.LongSum,
                    Points = [new MetricPointSnapshot { Tags = [], SumLong = 0 }],
                },
            ],
        };

        var output = PrometheusTextSerializer.Serialize(snapshot);

        Assert.Contains("# TYPE osvfs_projfs_errors_total counter\n", output, StringComparison.Ordinal);
        Assert.Contains("osvfs_projfs_errors_total 0\n", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_type_constant_matches_prometheus_v0_0_4()
    {
        Assert.Equal("text/plain; version=0.0.4; charset=utf-8", PrometheusTextSerializer.ContentType);
    }
}
