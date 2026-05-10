using OSVFS.Configuration;
using OSVFS.Telemetry;
using Xunit;

namespace OSVFS.UnitTests.Telemetry;

/// <summary>
/// Unit coverage for the small <c>host:port</c> parser the metrics
/// listener uses. Covers the loopback / wildcard / IPv6 forms operators
/// are most likely to type, and the malformed-input cases the loader
/// should reject before HttpListener is ever asked to bind.
/// </summary>
public class MetricsListenEndpointTests
{
    [Fact]
    public void Parse_returns_null_for_blank_input()
    {
        Assert.Null(MetricsListenEndpoint.Parse(null));
        Assert.Null(MetricsListenEndpoint.Parse(""));
        Assert.Null(MetricsListenEndpoint.Parse("   "));
    }

    [Fact]
    public void Parse_loopback_v4_yields_loopback_prefix()
    {
        var ep = MetricsListenEndpoint.Parse("127.0.0.1:9999");

        Assert.NotNull(ep);
        Assert.Equal("127.0.0.1", ep!.Host);
        Assert.Equal(9999, ep.Port);
        Assert.False(ep.IsWildcard);
        Assert.Equal("http://127.0.0.1:9999/", ep.UriPrefix);
    }

    [Fact]
    public void Parse_localhost_keeps_dns_name()
    {
        var ep = MetricsListenEndpoint.Parse("localhost:9090");

        Assert.NotNull(ep);
        Assert.Equal("localhost", ep!.Host);
        Assert.Equal(9090, ep.Port);
        Assert.False(ep.IsWildcard);
        Assert.Equal("http://localhost:9090/", ep.UriPrefix);
    }

    [Fact]
    public void Parse_wildcard_v4_marks_wildcard_and_uses_plus_prefix()
    {
        var ep = MetricsListenEndpoint.Parse("0.0.0.0:9999");

        Assert.NotNull(ep);
        Assert.True(ep!.IsWildcard);
        Assert.Equal("http://+:9999/", ep.UriPrefix);
    }

    [Fact]
    public void Parse_wildcard_plus_marks_wildcard()
    {
        var ep = MetricsListenEndpoint.Parse("+:9999");

        Assert.NotNull(ep);
        Assert.True(ep!.IsWildcard);
    }

    [Fact]
    public void Parse_ipv6_loopback_uses_bracketed_prefix()
    {
        var ep = MetricsListenEndpoint.Parse("[::1]:9999");

        Assert.NotNull(ep);
        Assert.False(ep!.IsWildcard);
        Assert.Equal("http://[::1]:9999/", ep.UriPrefix);
    }

    [Fact]
    public void Parse_ipv6_wildcard_marks_wildcard()
    {
        var ep = MetricsListenEndpoint.Parse("[::]:9999");
        Assert.NotNull(ep);
        Assert.True(ep!.IsWildcard);
    }

    [Theory]
    [InlineData("9999")]                  // missing host:port separator
    [InlineData("127.0.0.1")]             // missing :port
    [InlineData("127.0.0.1:")]            // empty port
    [InlineData("127.0.0.1:abc")]         // non-numeric port
    [InlineData("127.0.0.1:0")]           // out-of-range port
    [InlineData("127.0.0.1:65536")]       // out-of-range port
    [InlineData("[::1")]                  // unterminated v6 literal
    [InlineData("[::1]9999")]             // missing :port after v6 literal
    [InlineData(":9999")]                 // empty host
    public void Parse_throws_on_malformed_input(string input)
    {
        Assert.Throws<OsvfsConfigException>(() => MetricsListenEndpoint.Parse(input));
    }
}
