using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OSVFS.Configuration;
using OSVFS.Diagnostics;

namespace OSVFS.Telemetry;

/// <summary>
/// Builds and owns the OpenTelemetry pipeline (TracerProvider +
/// MeterProvider) for an OSVFS process invocation. Disposing the host
/// flushes pending spans / metrics through the OTLP exporter and tears
/// the providers down. When <c>[telemetry] metrics-listen</c> is
/// configured, the host also runs an in-process Prometheus
/// <c>/metrics</c> HTTP listener attached to the same MeterProvider.
/// </summary>
internal sealed class OsvfsTelemetryHost : IDisposable
{
    /// <summary>
    /// Default <c>service.name</c> resource attribute when the operator
    /// does not override it via <c>[telemetry] service-name</c>. Picked
    /// to match the assembly / executable name so dashboards remain
    /// readable out of the box.
    /// </summary>
    public const string DefaultServiceName = "osvfs";

    /// <summary>
    /// Backing TracerProvider. Null when the operator only configured
    /// the Prometheus listener (no OTLP endpoint); spans simply have no
    /// listener in that case.
    /// </summary>
    private readonly TracerProvider? tracerProvider;

    /// <summary>
    /// Backing MeterProvider. Always present whenever the host is
    /// constructed; disposed by <see cref="Dispose"/>.
    /// </summary>
    private readonly MeterProvider meterProvider;

    /// <summary>
    /// Local Prometheus listener. Null when the operator did not
    /// configure <c>metrics-listen</c>.
    /// </summary>
    private readonly OsvfsMetricsListener? metricsListener;

    private OsvfsTelemetryHost(
        TracerProvider? tracerProvider,
        MeterProvider meterProvider,
        OsvfsMetricsListener? metricsListener)
    {
        this.tracerProvider = tracerProvider;
        this.meterProvider = meterProvider;
        this.metricsListener = metricsListener;
    }

    /// <summary>
    /// Builds the OTel pipeline against <paramref name="config"/>. Returns
    /// null when neither OTLP nor the local Prometheus listener is
    /// configured, so the caller can short-circuit without paying the SDK
    /// initialization cost. Logging defaults to <see cref="NullLogger"/>
    /// for backward compatibility with callers that do not pass one.
    /// </summary>
    public static OsvfsTelemetryHost? Create(
        OsvfsTelemetryConfig? config, ILogger? logger = null)
    {
        if (config is null) return null;
        logger ??= NullLogger.Instance;

        var hasOtlp = !string.IsNullOrWhiteSpace(config.OtlpEndpoint);
        var metricsEndpoint = MetricsListenEndpoint.Parse(config.MetricsListen);
        if (!hasOtlp && metricsEndpoint is null) return null;

        Uri? otlpUri = null;
        OtlpExportProtocol otlpProtocol = OtlpExportProtocol.Grpc;
        if (hasOtlp)
        {
            if (!Uri.TryCreate(config.OtlpEndpoint, UriKind.Absolute, out otlpUri))
            {
                throw new OsvfsConfigException(
                    $"telemetry otlp-endpoint '{config.OtlpEndpoint}' is not a valid absolute URI.");
            }
            otlpProtocol = (config.OtlpProtocol ?? OtlpProtocolKind.Grpc) switch
            {
                OtlpProtocolKind.HttpProtobuf => OtlpExportProtocol.HttpProtobuf,
                _ => OtlpExportProtocol.Grpc,
            };
        }

        var serviceName = string.IsNullOrWhiteSpace(config.ServiceName)
            ? DefaultServiceName
            : config.ServiceName!;

        TracerProvider? tracerProvider = null;
        if (hasOtlp)
        {
            tracerProvider = Sdk.CreateTracerProviderBuilder()
                .ConfigureResource(b => b.AddService(serviceName))
                .AddSource(OsvfsTelemetry.S3SourceName)
                .AddSource(OsvfsTelemetry.ProjFsSourceName)
                .AddOtlpExporter(opt =>
                {
                    opt.Endpoint = otlpUri!;
                    opt.Protocol = otlpProtocol;
                })
                .Build()!;
        }

        SnapshotMetricExporter? snapshotExporter = null;
        BaseExportingMetricReader? snapshotReader = null;
        if (metricsEndpoint is not null)
        {
            snapshotExporter = new SnapshotMetricExporter();
            snapshotReader = new BaseExportingMetricReader(snapshotExporter);
        }

        var meterBuilder = Sdk.CreateMeterProviderBuilder()
            .ConfigureResource(b => b.AddService(serviceName))
            .AddMeter(OsvfsTelemetry.S3SourceName)
            .AddMeter(OsvfsTelemetry.ProjFsSourceName);

        if (hasOtlp)
        {
            meterBuilder.AddOtlpExporter(opt =>
            {
                opt.Endpoint = otlpUri!;
                opt.Protocol = otlpProtocol;
            });
        }
        if (snapshotReader is not null)
        {
            meterBuilder.AddReader(snapshotReader);
        }

        var meterProvider = meterBuilder.Build()!;

        OsvfsMetricsListener? metricsListener = null;
        if (metricsEndpoint is not null)
        {
            try
            {
                metricsListener = new OsvfsMetricsListener(
                    metricsEndpoint, snapshotReader!, snapshotExporter!, logger);
                metricsListener.Start();
            }
            catch (Exception ex)
            {
                // The listener failing must not abort startup of the trace pipeline;
                // log and continue with whatever we successfully built.
                logger.LogError(
                    ex,
                    "Prometheus /metrics listener failed to start at {Prefix}; metrics endpoint disabled.",
                    metricsEndpoint.UriPrefix);
                metricsListener?.Dispose();
                metricsListener = null;
            }
        }

        return new OsvfsTelemetryHost(tracerProvider, meterProvider, metricsListener);
    }

    /// <summary>
    /// Resolves the effective <see cref="OsvfsTelemetryConfig"/> from the
    /// CLI overrides layered on top of the file-derived
    /// <paramref name="fileConfig"/>. Returns null only when neither the
    /// OTLP endpoint nor the metrics-listen address is configured from
    /// any source so the caller can skip pipeline construction entirely.
    /// </summary>
    public static OsvfsTelemetryConfig? ResolveEffectiveConfig(
        OsvfsTelemetryConfig? fileConfig,
        string? cliOtlpEndpoint,
        string? cliMetricsListen)
    {
        var hasCliOtlp = !string.IsNullOrWhiteSpace(cliOtlpEndpoint);
        var hasCliMetrics = !string.IsNullOrWhiteSpace(cliMetricsListen);
        if (!hasCliOtlp && !hasCliMetrics)
        {
            return fileConfig;
        }

        return new OsvfsTelemetryConfig
        {
            OtlpEndpoint = hasCliOtlp ? cliOtlpEndpoint : fileConfig?.OtlpEndpoint,
            // CLI override only carries the endpoint string; preserve
            // protocol / service-name from the file when set.
            OtlpProtocol = fileConfig?.OtlpProtocol,
            ServiceName = fileConfig?.ServiceName,
            MetricsListen = hasCliMetrics ? cliMetricsListen : fileConfig?.MetricsListen,
        };
    }

    /// <summary>
    /// Disposes the listener and providers in reverse build order:
    /// stop accepting scrapes first, then flush spans, then metrics.
    /// Each call swallows exceptions so a noisy shutdown cannot mask
    /// the host's exit code.
    /// </summary>
    public void Dispose()
    {
        metricsListener?.Dispose();
        tracerProvider?.Dispose();
        meterProvider.Dispose();
    }
}
