using System.Security.Cryptography;
using System.Text;
using CrawlForge.Contracts.Security;

namespace CrawlForge.Mcp;

/// <summary>
/// Persistence port for the protected local MCP token. The default
/// implementation stores the ISecretProtector-protected representation in a
/// local file; the Host may bind a database-backed store at composition time.
/// Plaintext tokens are never persisted (FS-CRAWL-001 §9.1, §11.2).
/// </summary>
public interface IMcpTokenStore
{
    /// <summary>Loads the protected token representation, when present.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Protected representation, or null when none exists.</returns>
    Task<string?> LoadProtectedAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves the protected token representation.</summary>
    /// <param name="protectedToken">Protected representation (never plaintext).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the value is durable.</returns>
    Task SaveProtectedAsync(string protectedToken, CancellationToken cancellationToken = default);
}

/// <summary>
/// Local-file token store: writes only the protected representation, with
/// restrictive intent (the file holds ciphertext, never plaintext).
/// </summary>
public sealed class FileMcpTokenStore : IMcpTokenStore
{
    private readonly string _filePath;

    /// <summary>Creates the store.</summary>
    /// <param name="filePath">Local file path for the protected token.</param>
    public FileMcpTokenStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    /// <inheritdoc/>
    public async Task<string?> LoadProtectedAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        var text = await File.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <inheritdoc/>
    public async Task SaveProtectedAsync(string protectedToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedToken);
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(_filePath, protectedToken, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Locally generated MCP bearer tokens (FS-CRAWL-001 §11.2): random entropy,
/// encrypted at rest via <see cref="ISecretProtector"/>, constant-time
/// validation, and rotation that immediately invalidates prior clients
/// (criterion #16). No remote credential issuance exists (exclusion row 4).
/// </summary>
public sealed class McpTokenService
{
    private readonly ISecretProtector _protector;
    private readonly IMcpTokenStore _store;
    private readonly object _gate = new();
    private string _currentToken = string.Empty;

    /// <summary>Creates the token service.</summary>
    /// <param name="protector">Secret protector for encryption at rest.</param>
    /// <param name="store">Protected-token persistence port.</param>
    public McpTokenService(ISecretProtector protector, IMcpTokenStore store)
    {
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>The current bearer token (plaintext, in memory only).</summary>
    public string CurrentToken
    {
        get
        {
            lock (_gate)
            {
                return _currentToken;
            }
        }
    }

    /// <summary>Loads the persisted token or generates the first one.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when a token is active.</returns>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var protectedToken = await _store.LoadProtectedAsync(cancellationToken).ConfigureAwait(false);
        if (protectedToken is not null)
        {
            var plaintext = _protector.Unprotect(protectedToken);
            lock (_gate)
            {
                _currentToken = plaintext;
            }

            return;
        }

        await RotateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Validates a presented bearer token in constant time.</summary>
    /// <param name="token">Presented token, when any.</param>
    /// <returns>Whether the token matches the current one.</returns>
    public bool Validate(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        string current;
        lock (_gate)
        {
            current = _currentToken;
        }

        if (current.Length == 0)
        {
            return false;
        }

        var presented = Encoding.UTF8.GetBytes(token);
        var expected = Encoding.UTF8.GetBytes(current);
        return presented.Length == expected.Length
            && CryptographicOperations.FixedTimeEquals(presented, expected);
    }

    /// <summary>Rotates the token: the prior token is invalidated immediately
    /// and the new protected representation is persisted.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new plaintext token.</returns>
    public async Task<string> RotateAsync(CancellationToken cancellationToken = default)
    {
        var token = GenerateToken();
        var protectedToken = _protector.Protect(token);
        await _store.SaveProtectedAsync(protectedToken, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _currentToken = token;
        }

        return token;
    }

    /// <summary>Generates 256 bits of cryptographic randomness as a hex token.</summary>
    /// <returns>A fresh local token.</returns>
    public static string GenerateToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}
