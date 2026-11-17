using CrawlForge.Crawler.Proxy;
using Xunit;

namespace CrawlForge.Crawler.Tests.Rendering;

/// <summary>Pure routing-decision and upstream-URL parsing unit tests.</summary>
public sealed class ProxyRoutingUnitTests
{
    [Fact]
    public void Resolve_NoUpstream_IsAlwaysDirect()
    {
        var settings = new ProxyRoutingSettings();
        Assert.Equal(ProxyRoute.Direct, ProxyRouteResolver.Resolve("anything.example", settings));
    }

    [Fact]
    public void Resolve_UpstreamConfigured_UsesUpstream()
    {
        var settings = new ProxyRoutingSettings { UpstreamProxyUrl = "http://proxy.local:8080" };
        Assert.Equal(ProxyRoute.ViaUpstream, ProxyRouteResolver.Resolve("target.example", settings));
    }

    [Theory]
    [InlineData("exact.example", "exact.example", true)]
    [InlineData("exact.example", "other.example", false)]
    [InlineData(".example.com", "example.com", true)]
    [InlineData(".example.com", "sub.example.com", true)]
    [InlineData(".example.com", "notexample.com", false)]
    [InlineData("*.example.com", "sub.example.com", true)]
    [InlineData("*.example.com", "example.com", false)]
    [InlineData("*.example.com", "deep.sub.example.com", true)]
    [InlineData("EXAMPLE.COM", "example.com", true)]
    public void HostMatches_SupportsExactSuffixAndWildcard(string pattern, string host, bool expected)
    {
        Assert.Equal(expected, ProxyRouteResolver.HostMatches(pattern, host));
    }

    [Fact]
    public void Resolve_BypassList_ExemptsHosts()
    {
        var settings = new ProxyRoutingSettings
        {
            UpstreamProxyUrl = "socks5://proxy.local:1080",
            BypassHosts = [".internal.example", "exact.example"],
        };
        Assert.Equal(ProxyRoute.Direct, ProxyRouteResolver.Resolve("host.internal.example", settings));
        Assert.Equal(ProxyRoute.Direct, ProxyRouteResolver.Resolve("exact.example", settings));
        Assert.Equal(ProxyRoute.ViaUpstream, ProxyRouteResolver.Resolve("public.example", settings));
    }

    [Theory]
    [InlineData("http://proxy.local", "http", "proxy.local", 80)]
    [InlineData("https://proxy.local", "https", "proxy.local", 443)]
    [InlineData("socks5://proxy.local", "socks5", "proxy.local", 1080)]
    [InlineData("socks4://proxy.local:9999", "socks4", "proxy.local", 9999)]
    [InlineData("http://proxy.local:8080", "http", "proxy.local", 8080)]
    public void Parse_SchemesAndDefaultPorts(string url, string scheme, string host, int port)
    {
        var parsed = UpstreamProxyParser.Parse(url);
        Assert.Equal(scheme, parsed.Scheme);
        Assert.Equal(host, parsed.Host);
        Assert.Equal(port, parsed.Port);
        Assert.False(parsed.HasCredentials);
    }

    [Fact]
    public void Parse_ExtractsUserSuppliedCredentials()
    {
        var parsed = UpstreamProxyParser.Parse("socks5://user:p%40ss@proxy.local:1080");
        Assert.True(parsed.HasCredentials);
        Assert.Equal("user", parsed.UserName);
        Assert.Equal("p@ss", parsed.Password);
        Assert.True(parsed.IsSocks);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://proxy.local")]
    public void Parse_RejectsInvalidConfiguration(string url)
    {
        Assert.Throws<ArgumentException>(() => UpstreamProxyParser.Parse(url));
    }
}
