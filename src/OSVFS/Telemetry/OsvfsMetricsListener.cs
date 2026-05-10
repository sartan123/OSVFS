using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;

namespace OSVFS.Telemetry;

/// <summary>
/// Hosts an <see cref="HttpListener"/> bound to the operator-supplied
/// loopback address and serves three endpoints:
/// <list type="bullet">
///   <item><description><c>/metrics</c> — Prometheus text exposition; pulls a fresh snapshot per request via <see cref="MetricReader.Collect"/>.</description></item>
///   <item><description><c>/healthz</c> — flat-text liveness probe (always <c>200 OK\nok</c>).</description></item>
///   <item><description><c>/version</c> — assembly informational version (or <c>"0.0.0"</c> when stripped).</description></item>
/// </list>
/// All other paths return <c>404</c> so a misconfigured Prometheus target
/// fails fast.
/// </summary>
internal sealed class OsvfsMetricsListener : IDisposable
{
    /// <summary>
    /// HttpListener wrapping the configured prefix. Lifetime mirrors the
    /// listener instance: started in <see cref="Start"/>, stopped in
    /// <see cref="Dispose"/>.
    /// </summary>
    private readonly HttpListener listener = new();

    /// <summary>
    /// Pull-trigger for the captured metrics. We call
    /// <see cref="MetricReader.Collect"/> per scrape to push a fresh
    /// snapshot through <see cref="exporter"/>.
    /// </summary>
    private readonly MetricReader reader;

    /// <summary>
    /// Snapshot exporter wired into the same MeterProvider as
    /// <see cref="reader"/>.
    /// </summary>
    private readonly SnapshotMetricExporter exporter;

    /// <summary>
    /// Logger used for startup banners and request error reporting.
    /// </summary>
    private readonly ILogger logger;

    /// <summary>
    /// Cooperative shutdown signal for the accept loop. Cancelled inside
    /// <see cref="Dispose"/> so a stuck <c>GetContextAsync</c> unblocks
    /// when the listener closes.
    /// </summary>
    private readonly CancellationTokenSource cts = new();

    /// <summary>
    /// Cached version string returned by <c>/version</c>. Populated once
    /// at construction time so the request handler stays allocation-free.
    /// </summary>
    private readonly string version;

    /// <summary>
    /// Accept loop task started by <see cref="Start"/>. Held so
    /// <see cref="Dispose"/> can wait briefly for a clean exit.
    /// </summary>
    private Task? loop;

    /// <summary>
    /// Endpoint used by tests to read back the bound URL prefix.
    /// </summary>
    public MetricsListenEndpoint Endpoint { get; }

    /// <summary>
    /// Builds the listener for <paramref name="endpoint"/> wired to
    /// <paramref name="reader"/> and <paramref name="exporter"/>. The
    /// listener itself is not started until <see cref="Start"/> runs so
    /// the host can construct it at any time during pipeline build.
    /// </summary>
    public OsvfsMetricsListener(
        MetricsListenEndpoint endpoint,
        MetricReader reader,
        SnapshotMetricExporter exporter,
        ILogger logger)
    {
        Endpoint = endpoint;
        this.reader = reader;
        this.exporter = exporter;
        this.logger = logger;
        listener.Prefixes.Add(endpoint.UriPrefix);
        version = ResolveVersion();
    }

    /// <summary>
    /// Opens the HTTP socket and starts the accept loop. Throws when the
    /// port is busy or the prefix has not been ACL'd (HttpListener
    /// requires <c>netsh http add urlacl</c> for non-loopback prefixes
    /// when running as a non-admin user); the host logs and continues
    /// without a metrics endpoint in that case.
    /// </summary>
    public void Start()
    {
        listener.Start();
        loop = Task.Run(() => AcceptLoopAsync(cts.Token));
        logger.LogInformation(
            "Prometheus /metrics listener started at {Prefix}", Endpoint.UriPrefix);
        if (Endpoint.IsWildcard)
        {
            logger.LogWarning(
                "Metrics listener is bound to a wildcard host ({Host}). The /metrics endpoint " +
                "is now reachable on every network interface; restrict access via the host firewall.",
                Endpoint.Host);
        }
    }

    /// <summary>
    /// Pulls contexts off the listener and dispatches them to
    /// <see cref="HandleRequest"/> on the thread pool. Tolerates the
    /// expected shutdown exceptions from <see cref="HttpListener"/> so
    /// the loop returns cleanly when <see cref="Dispose"/> stops the
    /// listener.
    /// </summary>
    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (InvalidOperationException) { break; }

            // Fire-and-forget: the response is fully synchronous so we don't
            // need to await it, and a slow scrape should not stall the next.
            _ = Task.Run(() => HandleRequest(context), token);
        }
    }

    /// <summary>
    /// Routes <paramref name="context"/> to the matching endpoint
    /// handler. Catches and logs any failure so a single bad request
    /// cannot tear the listener down — the Prometheus scraper will retry
    /// on its own cadence.
    /// </summary>
    private void HandleRequest(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            switch (path)
            {
                case "/metrics":
                    HandleMetrics(context);
                    break;
                case "/healthz":
                    WriteText(context, 200, "text/plain; charset=utf-8", "ok\n");
                    break;
                case "/version":
                    WriteText(context, 200, "text/plain; charset=utf-8", version + "\n");
                    break;
                default:
                    WriteText(context, 404, "text/plain; charset=utf-8", "not found\n");
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "metrics-listener: request handling failed");
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch { /* response may already be disposed */ }
        }
    }

    /// <summary>
    /// Forces a metric collection cycle and writes the resulting
    /// Prometheus payload. <see cref="MetricReader.Collect"/> drives the
    /// exporter synchronously so the snapshot we read back covers the
    /// instruments observed up to this scrape.
    /// </summary>
    private void HandleMetrics(HttpListenerContext context)
    {
        reader.Collect(timeoutMilliseconds: 5_000);
        var snapshot = exporter.GetSnapshot();
        var body = PrometheusTextSerializer.Serialize(snapshot);
        WriteText(context, 200, PrometheusTextSerializer.ContentType, body);
    }

    /// <summary>
    /// Writes <paramref name="body"/> as a UTF-8 response, sets the
    /// status code and content type, and closes the stream. Splits the
    /// flush + close so a partial write surfaces as a logged exception
    /// rather than a silent half-response.
    /// </summary>
    private static void WriteText(HttpListenerContext context, int status, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        context.Response.OutputStream.Close();
    }

    /// <summary>
    /// Resolves the assembly informational version of the host
    /// executable so <c>/version</c> reports the same string the OTel
    /// resource carries. Falls back to <c>"0.0.0"</c> when the
    /// attribute has been stripped (AOT publish defaults).
    /// </summary>
    private static string ResolveVersion() =>
        typeof(OsvfsMetricsListener).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? "0.0.0";

    /// <summary>
    /// Stops the listener, signals the accept loop to exit, and waits
    /// briefly for a clean teardown. Each step is wrapped in a catch so
    /// a stuck shutdown cannot mask the host's exit code.
    /// </summary>
    public void Dispose()
    {
        try { cts.Cancel(); } catch { /* already disposed */ }
        try { listener.Stop(); } catch { /* not started */ }
        try { listener.Close(); } catch { /* already closed */ }
        try { loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* loop already exited or threw */ }
        cts.Dispose();
        Debug.Assert(!listener.IsListening);
    }
}
