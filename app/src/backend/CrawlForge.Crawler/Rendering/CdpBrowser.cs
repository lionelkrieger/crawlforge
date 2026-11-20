using System.Text.Json;

namespace CrawlForge.Crawler.Rendering;

/// <summary>
/// Browser-level CDP control over one long-lived browser process (D-002):
/// Target-domain context/target lifecycle with flat-mode session attachment.
/// Every page renders in a FRESH browser context (isolated cookies, storage
/// and cache — §5.6.2); the context is disposed when the page closes.
/// </summary>
public sealed class CdpBrowser : IAsyncDisposable
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    private readonly CdpConnection _connection;
    private readonly bool _ownsConnection;
    private int _disposed;

    /// <summary>Creates the wrapper over an open connection.</summary>
    /// <param name="connection">The connected CDP client.</param>
    /// <param name="ownsConnection">Whether dispose closes the connection.</param>
    public CdpBrowser(CdpConnection connection, bool ownsConnection = true)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
        _ownsConnection = ownsConnection;
    }

    /// <summary>The underlying CDP connection (event subscription surface).</summary>
    public CdpConnection Connection => _connection;

    /// <summary>Whether the browser connection is still open.</summary>
    public bool IsConnected => Volatile.Read(ref _disposed) == 0 && _connection.IsConnected;

    /// <summary>Connects to a local browser DevTools endpoint.</summary>
    /// <param name="endpoint">Loopback ws:// endpoint.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The connected browser wrapper.</returns>
    public static async Task<CdpBrowser> ConnectAsync(Uri endpoint, CancellationToken cancellationToken = default)
    {
        var connection = new CdpConnection();
        try
        {
            await connection.ConnectAsync(endpoint, cancellationToken);
            return new CdpBrowser(connection);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Reads the browser product/version (Browser.getVersion).</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Product, protocol version and user agent.</returns>
    public async Task<(string Product, string UserAgent)> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        var result = await SendAsync("Browser.getVersion", null, null, cancellationToken);
        var product = result.TryGetProperty("product", out var p) ? p.GetString() ?? "unknown" : "unknown";
        var userAgent = result.TryGetProperty("userAgent", out var u) ? u.GetString() ?? string.Empty : string.Empty;
        return (product, userAgent);
    }

    /// <summary>Creates a fresh isolated browser context (Target.createBrowserContext).</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The browser context id.</returns>
    public async Task<string> CreateBrowserContextAsync(CancellationToken cancellationToken = default)
    {
        var result = await SendAsync("Target.createBrowserContext", new Dictionary<string, object?>
        {
            ["disposeOnDetach"] = false,
        }, null, cancellationToken);
        return result.GetProperty("browserContextId").GetString()
            ?? throw new CdpCommandException("Target.createBrowserContext returned no browserContextId.");
    }

    /// <summary>Creates a page target inside a context (Target.createTarget).</summary>
    /// <param name="browserContextId">Owning fresh context.</param>
    /// <param name="url">Initial target URL (about:blank before navigation).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The target id.</returns>
    public async Task<string> CreateTargetAsync(string browserContextId, string url, CancellationToken cancellationToken = default)
    {
        var result = await SendAsync("Target.createTarget", new Dictionary<string, object?>
        {
            ["url"] = url,
            ["browserContextId"] = browserContextId,
        }, null, cancellationToken);
        return result.GetProperty("targetId").GetString()
            ?? throw new CdpCommandException("Target.createTarget returned no targetId.");
    }

    /// <summary>Attaches a flat-mode session to a target (Target.attachToTarget).</summary>
    /// <param name="targetId">The page target.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The session id used for page-scoped commands.</returns>
    public async Task<string> AttachAsync(string targetId, CancellationToken cancellationToken = default)
    {
        var result = await SendAsync("Target.attachToTarget", new Dictionary<string, object?>
        {
            ["targetId"] = targetId,
            ["flatten"] = true,
        }, null, cancellationToken);
        return result.GetProperty("sessionId").GetString()
            ?? throw new CdpCommandException("Target.attachToTarget returned no sessionId.");
    }

    /// <summary>Closes a page target (Target.closeTarget).</summary>
    /// <param name="targetId">The target.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task CloseTargetAsync(string targetId, CancellationToken cancellationToken = default)
    {
        await SendAsync("Target.closeTarget", new Dictionary<string, object?>
        {
            ["targetId"] = targetId,
        }, null, cancellationToken);
    }

    /// <summary>Disposes a browser context (Target.disposeBrowserContext).</summary>
    /// <param name="browserContextId">The context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task DisposeBrowserContextAsync(string browserContextId, CancellationToken cancellationToken = default)
    {
        await SendAsync("Target.disposeBrowserContext", new Dictionary<string, object?>
        {
            ["browserContextId"] = browserContextId,
        }, null, cancellationToken);
    }

    /// <summary>Sends a page-scoped command on the flat session.</summary>
    /// <param name="method">CDP method.</param>
    /// <param name="parameters">CamelCase parameters, when any.</param>
    /// <param name="sessionId">Flat session id, when page-scoped.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result payload.</returns>
    public Task<JsonElement> SendAsync(
        string method,
        IReadOnlyDictionary<string, object?>? parameters,
        string? sessionId,
        CancellationToken cancellationToken = default) =>
        _connection.SendCommandAsync(method, parameters, sessionId, CommandTimeout, cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_ownsConnection)
        {
            await _connection.DisposeAsync();
        }
    }
}
