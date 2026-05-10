using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using OSVFS.Configuration;
using OSVFS.Diagnostics;
using OSVFS.Telemetry;
using Xunit;

namespace OSVFS.UnitTests.Telemetry;

/// <summary>
/// End-to-end integration coverage for the metrics listener. Spins up
/// the OTel pipeline + listener, records a measurement, and scrapes
/// <c>/metrics</c> with <see cref="HttpClient"/>. The supporting
/// endpoints (<c>/healthz</c>, <c>/version</c>) are exercised in the
/// same fixture so the listener lifecycle is paid once per run.
/// </summary>
public class OsvfsMetricsListenerTests
{
    /// <summary>
    /// Binds an ephemeral loopback port via <see cref="TcpListener"/>,
    /// records the assigned port, and releases the socket so
    /// <see cref="HttpListener"/> can re-bind it. Standard "find a free
    /// port" trick — there's a tiny race between Stop() and the
    /// HttpListener bind, but it's good enough for a single-host test.
    /// </summary>
    private static int LeaseLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    [Fact]
    public async Task Listener_serves_metrics_healthz_and_version()
    {
        var port = LeaseLoopbackPort();
        var config = new OsvfsTelemetryConfig { MetricsListen = $"127.0.0.1:{port}" };

        using var host = OsvfsTelemetryHost.Create(config, NullLogger.Instance);
        Assert.NotNull(host);

        // Record one S3 byte-upload sample so /metrics has something to render.
        OsvfsTelemetry.BytesUploaded.Add(2048);

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        client.Timeout = TimeSpan.FromSeconds(5);

        var healthz = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, healthz.StatusCode);
        Assert.Equal("ok\n", await healthz.Content.ReadAsStringAsync());

        var version = await client.GetAsync("/version");
        Assert.Equal(HttpStatusCode.OK, version.StatusCode);
        var versionBody = await version.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrWhiteSpace(versionBody));

        var metrics = await client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, metrics.StatusCode);
        Assert.Equal("text/plain", metrics.Content.Headers.ContentType?.MediaType);
        var metricsBody = await metrics.Content.ReadAsStringAsync();
        Assert.Contains("# TYPE osvfs_s3_bytes_uploaded_bytes_total counter", metricsBody, StringComparison.Ordinal);
        Assert.Contains("osvfs_s3_bytes_uploaded_bytes_total 2048", metricsBody, StringComparison.Ordinal);

        var notFound = await client.GetAsync("/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
    }

    [Fact]
    public void Create_without_endpoints_returns_null()
    {
        Assert.Null(OsvfsTelemetryHost.Create(new OsvfsTelemetryConfig()));
    }

    [Fact]
    public void Create_with_only_metrics_listen_does_not_require_otlp_endpoint()
    {
        var port = LeaseLoopbackPort();
        var config = new OsvfsTelemetryConfig { MetricsListen = $"127.0.0.1:{port}" };

        using var host = OsvfsTelemetryHost.Create(config, NullLogger.Instance);

        Assert.NotNull(host);
    }
}
