using CrawlForge.Mcp;
using CrawlForge.Security.Secrets;
using Xunit;

namespace CrawlForge.Mcp.Tests;

/// <summary>
/// Local token lifecycle (FS-CRAWL-001 §9.1, §11.2; criterion #16): random
/// generation, encryption at rest, constant-time validation, and rotation
/// that immediately invalidates prior clients.
/// </summary>
public class McpTokenServiceTests : IDisposable
{
    private readonly string _directory;
    private readonly AppEntropySecretProtector _protector;

    /// <summary>Creates the suite with a local entropy store.</summary>
    public McpTokenServiceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "cf-mcp-tokens-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _protector = new AppEntropySecretProtector(new EntropyKeyStore(Path.Combine(_directory, "entropy.key")));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private McpTokenService CreateService(string name = "token.tok") =>
        new(_protector, new FileMcpTokenStore(Path.Combine(_directory, name)));

    [Fact]
    public async Task InitializeGeneratesHighEntropyToken()
    {
        var service = CreateService();
        await service.InitializeAsync();
        var token = service.CurrentToken;
        Assert.Equal(64, token.Length); // 256 bits hex
        Assert.Matches("^[0-9a-f]{64}$", token);

        var second = CreateService("token2.tok");
        await second.InitializeAsync();
        Assert.NotEqual(token, second.CurrentToken);
    }

    [Fact]
    public async Task TokenIsEncryptedAtRestNeverPlaintext()
    {
        var path = Path.Combine(_directory, "rest.tok");
        var service = new McpTokenService(_protector, new FileMcpTokenStore(path));
        await service.InitializeAsync();

        var stored = await File.ReadAllTextAsync(path);
        Assert.NotEqual(service.CurrentToken, stored);
        Assert.StartsWith(AppEntropySecretProtector.SchemePrefix, stored);
        Assert.Equal(service.CurrentToken, _protector.Unprotect(stored));
    }

    [Fact]
    public async Task PersistedTokenSurvivesRestart()
    {
        var first = CreateService("persist.tok");
        await first.InitializeAsync();
        var token = first.CurrentToken;

        var restarted = CreateService("persist.tok");
        await restarted.InitializeAsync();
        Assert.Equal(token, restarted.CurrentToken);
        Assert.True(restarted.Validate(token));
    }

    [Fact]
    public async Task RotationInvalidatesPriorTokenImmediately()
    {
        var service = CreateService("rotate.tok");
        await service.InitializeAsync();
        var oldToken = service.CurrentToken;
        Assert.True(service.Validate(oldToken));

        var newToken = await service.RotateAsync();
        Assert.NotEqual(oldToken, newToken);
        Assert.False(service.Validate(oldToken));   // criterion #16
        Assert.True(service.Validate(newToken));
    }

    [Fact]
    public async Task RotationTokenIsPersistedProtected()
    {
        var path = Path.Combine(_directory, "rotate-rest.tok");
        var service = new McpTokenService(_protector, new FileMcpTokenStore(path));
        await service.InitializeAsync();
        var newToken = await service.RotateAsync();

        var stored = await File.ReadAllTextAsync(path);
        Assert.StartsWith(AppEntropySecretProtector.SchemePrefix, stored);
        Assert.Equal(newToken, _protector.Unprotect(stored));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-token")]
    public async Task InvalidTokensAreRejected(string? presented)
    {
        var service = CreateService("validate.tok");
        await service.InitializeAsync();
        Assert.False(service.Validate(presented));
    }

    [Fact]
    public void ValidationBeforeInitializationRejectsEverything()
    {
        var service = CreateService("uninit.tok");
        Assert.False(service.Validate("anything"));
    }

    [Fact]
    public async Task TruncatedTokenIsRejected()
    {
        var service = CreateService("trunc.tok");
        await service.InitializeAsync();
        Assert.False(service.Validate(service.CurrentToken[..^1]));
    }
}
