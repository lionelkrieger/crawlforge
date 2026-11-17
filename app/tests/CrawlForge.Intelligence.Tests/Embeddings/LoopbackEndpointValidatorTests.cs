using CrawlForge.Intelligence.Embeddings;
using Xunit;

namespace CrawlForge.Intelligence.Tests.Embeddings;

/// <summary>
/// Loopback-only address validation (FS-CRAWL-002 §12.2, exclusion E7,
/// criterion #45): any non-loopback endpoint is REJECTED at configuration.
/// </summary>
public class LoopbackEndpointValidatorTests
{
    [Theory]
    [InlineData("http://127.0.0.1:11434")]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://127.0.0.53:8080")]
    [InlineData("http://127.255.255.254:9999")]
    [InlineData("http://[::1]:11434")]
    [InlineData("http://localhost:11434")]
    [InlineData("http://LOCALHOST:1234")]
    [InlineData("https://127.0.0.1:8443")]
    public void LoopbackEndpointsAreAccepted(string endpoint)
    {
        var uri = LoopbackEndpointValidator.Validate(endpoint);
        Assert.True(LoopbackEndpointValidator.IsLoopbackHost(uri.Host));
        Assert.True(LoopbackEndpointValidator.TryValidate(endpoint, out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("http://192.168.1.5:11434")]   // private LAN — not loopback
    [InlineData("http://10.0.0.1:11434")]      // private LAN
    [InlineData("http://172.16.0.1:11434")]    // private LAN
    [InlineData("http://8.8.8.8:11434")]       // public
    [InlineData("https://embeddings.example.test/v1")] // remote host
    [InlineData("http://169.254.1.1:11434")]   // link-local
    [InlineData("http://0.0.0.0:11434")]       // unspecified — not loopback
    [InlineData("http://[2001:db8::1]:11434")] // public IPv6
    [InlineData("ftp://127.0.0.1:21")]         // wrong scheme
    [InlineData("not-a-uri")]
    [InlineData("")]
    [InlineData(null)]
    public void NonLoopbackEndpointsAreRejected(string? endpoint)
    {
        Assert.Throws<EmbeddingConfigurationException>(() => LoopbackEndpointValidator.Validate(endpoint));
        Assert.False(LoopbackEndpointValidator.TryValidate(endpoint, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void RejectionMessageNamesTheBoundary()
    {
        var ex = Assert.Throws<EmbeddingConfigurationException>(
            () => LoopbackEndpointValidator.Validate("https://embeddings.example.test"));
        Assert.Contains("loopback", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("E7", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalhostResolvesToLoopbackIpWithoutDns()
    {
        var uri = LoopbackEndpointValidator.Validate("http://localhost:11434");
        var address = LoopbackEndpointValidator.ResolveLoopbackAddress(uri);
        Assert.Equal(System.Net.IPAddress.Loopback, address);
    }

    [Fact]
    public void LiteralLoopbackAddressesPassThrough()
    {
        var uri = LoopbackEndpointValidator.Validate("http://127.0.0.53:11434");
        Assert.Equal(System.Net.IPAddress.Parse("127.0.0.53"), LoopbackEndpointValidator.ResolveLoopbackAddress(uri));
    }
}
