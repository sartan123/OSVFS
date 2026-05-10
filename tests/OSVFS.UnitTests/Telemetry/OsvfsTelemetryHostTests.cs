using OSVFS.Configuration;
using OSVFS.Telemetry;
using Xunit;

namespace OSVFS.UnitTests.Telemetry;

/// <summary>
/// Verifies the small amount of resolution and validation logic the
/// telemetry host owns. The OpenTelemetry pipeline itself is not built
/// here because the OTLP exporter would attempt to dial out on construction;
/// integration coverage lives next to the S3 backend tests.
/// </summary>
public class OsvfsTelemetryHostTests
{
    [Fact]
    public void Create_returns_null_when_no_endpoint_configured()
    {
        Assert.Null(OsvfsTelemetryHost.Create(null));
        Assert.Null(OsvfsTelemetryHost.Create(new OsvfsTelemetryConfig()));
        Assert.Null(OsvfsTelemetryHost.Create(new OsvfsTelemetryConfig { OtlpEndpoint = "" }));
        Assert.Null(OsvfsTelemetryHost.Create(new OsvfsTelemetryConfig { OtlpEndpoint = "   " }));
    }

    [Fact]
    public void Create_throws_when_endpoint_is_not_an_absolute_uri()
    {
        var ex = Assert.Throws<OsvfsConfigException>(() =>
            OsvfsTelemetryHost.Create(new OsvfsTelemetryConfig { OtlpEndpoint = "not-a-uri" }));
        Assert.Contains("not-a-uri", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveEffectiveConfig_returns_file_when_no_cli_override()
    {
        var fileConfig = new OsvfsTelemetryConfig
        {
            OtlpEndpoint = "http://collector:4317",
            OtlpProtocol = OtlpProtocolKind.Grpc,
            ServiceName = "from-file",
        };

        var effective = OsvfsTelemetryHost.ResolveEffectiveConfig(fileConfig, null, null);

        Assert.Same(fileConfig, effective);
    }

    [Fact]
    public void ResolveEffectiveConfig_substitutes_cli_endpoint_but_keeps_file_protocol_and_service()
    {
        var fileConfig = new OsvfsTelemetryConfig
        {
            OtlpEndpoint = "http://from-file:4317",
            OtlpProtocol = OtlpProtocolKind.HttpProtobuf,
            ServiceName = "preserved-service",
            MetricsListen = "127.0.0.1:9999",
        };

        var effective = OsvfsTelemetryHost.ResolveEffectiveConfig(fileConfig, "http://from-cli:4318", null);

        Assert.NotNull(effective);
        Assert.Equal("http://from-cli:4318", effective!.OtlpEndpoint);
        Assert.Equal(OtlpProtocolKind.HttpProtobuf, effective.OtlpProtocol);
        Assert.Equal("preserved-service", effective.ServiceName);
        Assert.Equal("127.0.0.1:9999", effective.MetricsListen);
    }

    [Fact]
    public void ResolveEffectiveConfig_substitutes_cli_metrics_listen_but_keeps_file_endpoint()
    {
        var fileConfig = new OsvfsTelemetryConfig
        {
            OtlpEndpoint = "http://from-file:4317",
            OtlpProtocol = OtlpProtocolKind.Grpc,
            ServiceName = "preserved-service",
            MetricsListen = "127.0.0.1:9999",
        };

        var effective = OsvfsTelemetryHost.ResolveEffectiveConfig(fileConfig, null, "0.0.0.0:9090");

        Assert.NotNull(effective);
        Assert.Equal("http://from-file:4317", effective!.OtlpEndpoint);
        Assert.Equal(OtlpProtocolKind.Grpc, effective.OtlpProtocol);
        Assert.Equal("preserved-service", effective.ServiceName);
        Assert.Equal("0.0.0.0:9090", effective.MetricsListen);
    }

    [Fact]
    public void ResolveEffectiveConfig_returns_file_with_metrics_only()
    {
        var fileConfig = new OsvfsTelemetryConfig { MetricsListen = "127.0.0.1:9999" };

        var effective = OsvfsTelemetryHost.ResolveEffectiveConfig(fileConfig, null, null);

        Assert.Same(fileConfig, effective);
    }

    [Fact]
    public void ResolveEffectiveConfig_cli_metrics_listen_alone_creates_config()
    {
        var effective = OsvfsTelemetryHost.ResolveEffectiveConfig(null, null, "127.0.0.1:9999");

        Assert.NotNull(effective);
        Assert.Null(effective!.OtlpEndpoint);
        Assert.Equal("127.0.0.1:9999", effective.MetricsListen);
    }

    [Fact]
    public void ResolveEffectiveConfig_returns_null_when_neither_source_supplies_endpoint()
    {
        Assert.Null(OsvfsTelemetryHost.ResolveEffectiveConfig(null, null, null));
        Assert.Null(OsvfsTelemetryHost.ResolveEffectiveConfig(null, "   ", "   "));
    }

    [Fact]
    public void Create_returns_null_when_only_metrics_listen_is_blank_and_no_otlp()
    {
        Assert.Null(OsvfsTelemetryHost.Create(new OsvfsTelemetryConfig { MetricsListen = "   " }));
    }
}
