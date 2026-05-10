using System.Net;
using OSVFS.Configuration;

namespace OSVFS.Telemetry;

/// <summary>
/// Validated <c>host:port</c> address for the local Prometheus
/// <c>/metrics</c> listener. Accepts loopback IP literals
/// (<c>127.0.0.1</c>, <c>[::1]</c>), <c>localhost</c>, and the wildcard
/// <c>0.0.0.0</c> / <c>+</c> / <c>*</c>; the wildcard surfaces a warning
/// in the host log because it exposes internal counters to the network.
/// </summary>
internal sealed class MetricsListenEndpoint
{
    /// <summary>
    /// Host portion of the listener address, normalized for use inside an
    /// <see cref="HttpListener"/> URI prefix.
    /// </summary>
    public string Host { get; }

    /// <summary>
    /// Port portion of the listener address.
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// True when the parsed host is the wildcard binding (<c>+</c>,
    /// <c>0.0.0.0</c>, <c>::</c>, <c>*</c>) and therefore exposes the
    /// listener on every interface. Surfaced so the host can warn the
    /// operator before opening the port.
    /// </summary>
    public bool IsWildcard { get; }

    /// <summary>
    /// HttpListener URI prefix derived from <see cref="Host"/> and
    /// <see cref="Port"/>. Always ends with <c>/</c> per the
    /// <see cref="HttpListenerPrefixCollection"/> contract.
    /// </summary>
    public string UriPrefix { get; }

    private MetricsListenEndpoint(string host, int port, bool isWildcard, string uriPrefix)
    {
        Host = host;
        Port = port;
        IsWildcard = isWildcard;
        UriPrefix = uriPrefix;
    }

    /// <summary>
    /// Parses <paramref name="value"/> in <c>host:port</c> form. Throws
    /// <see cref="OsvfsConfigException"/> with a remediation message when
    /// the value is malformed; returns null when <paramref name="value"/>
    /// itself is null or whitespace so the caller can short-circuit
    /// without paying the parse cost.
    /// </summary>
    public static MetricsListenEndpoint? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var trimmed = value.Trim();
        // IPv6 literals must be written as "[::1]:9999" — strip the brackets and
        // route the residue through Uri parsing for a uniform host/port split.
        var hostPart = trimmed;
        var portPart = string.Empty;
        if (trimmed.StartsWith('['))
        {
            var closing = trimmed.IndexOf(']');
            if (closing <= 0)
            {
                throw new OsvfsConfigException(
                    $"telemetry metrics-listen '{value}' is malformed: " +
                    "IPv6 literals must use [::1]:port form.");
            }
            hostPart = trimmed.Substring(1, closing - 1);
            var rest = trimmed[(closing + 1)..];
            if (!rest.StartsWith(':'))
            {
                throw new OsvfsConfigException(
                    $"telemetry metrics-listen '{value}' is malformed: missing ':port' after the IPv6 literal.");
            }
            portPart = rest[1..];
        }
        else
        {
            var colon = trimmed.LastIndexOf(':');
            if (colon < 0)
            {
                throw new OsvfsConfigException(
                    $"telemetry metrics-listen '{value}' is malformed: expected 'host:port' (e.g. 127.0.0.1:9999).");
            }
            hostPart = trimmed[..colon];
            portPart = trimmed[(colon + 1)..];
        }

        if (!int.TryParse(portPart, out var port) || port is < 1 or > 65535)
        {
            throw new OsvfsConfigException(
                $"telemetry metrics-listen '{value}' has invalid port '{portPart}': must be an integer in 1..65535.");
        }

        var (normalized, isWildcard, prefixHost) = NormalizeHost(hostPart, value);
        var uriPrefix = $"http://{prefixHost}:{port}/";
        return new MetricsListenEndpoint(normalized, port, isWildcard, uriPrefix);
    }

    /// <summary>
    /// Maps the raw host token to the form HttpListener accepts. The
    /// returned tuple carries:
    /// <list type="bullet">
    ///   <item><description><c>normalized</c> — host string surfaced via <see cref="Host"/>.</description></item>
    ///   <item><description><c>isWildcard</c> — true when the binding is on every interface.</description></item>
    ///   <item><description><c>prefixHost</c> — the literal HttpListener accepts inside the URI prefix.</description></item>
    /// </list>
    /// </summary>
    private static (string normalized, bool isWildcard, string prefixHost) NormalizeHost(string host, string original)
    {
        if (host.Length == 0)
        {
            throw new OsvfsConfigException(
                $"telemetry metrics-listen '{original}' is malformed: host portion is empty.");
        }

        if (host == "*" || host == "+" || host == "0.0.0.0")
        {
            // HttpListener treats "+" as the strong wildcard (any host header)
            // and "*" as the weak wildcard. Use the strong form so the listener
            // picks up requests for any name resolving to a local interface.
            return (host, true, "+");
        }

        if (host == "::" || host == "[::]")
        {
            return (host, true, "+");
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return ("localhost", false, "localhost");
        }

        if (IPAddress.TryParse(host, out var ip))
        {
            // HttpListener requires IPv6 literals inside the URI to be bracketed.
            return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? (ip.ToString(), false, $"[{ip}]")
                : (ip.ToString(), false, ip.ToString());
        }

        // DNS hostname — accept it as-is. HttpListener resolves it at Start().
        return (host, false, host);
    }
}
