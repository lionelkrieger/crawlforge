using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CrawlForge.ContractConformance.Tests.Harness;

/// <summary>One MCP JSON-RPC exchange (request + parsed response + session id).</summary>
/// <param name="RequestJson">The request payload sent.</param>
/// <param name="Status">HTTP status code.</param>
/// <param name="ResponseJson">Raw response body.</param>
/// <param name="Root">Parsed response root.</param>
/// <param name="SessionId">Mcp-Session-Id response header, when present.</param>
public sealed record McpExchange(string RequestJson, int Status, string ResponseJson, JsonElement Root, string? SessionId)
{
    /// <summary>The result object (throws when the response is an error).</summary>
    public JsonElement Result => Root.GetProperty("result");

    /// <summary>True when the response carries a JSON-RPC error object.</summary>
    public bool IsError => Root.TryGetProperty("error", out _);
}

/// <summary>
/// Streamable-HTTP MCP client (FS-CRAWL-001 §11.2): POST {endpoint} with the
/// local bearer token; the initialize response assigns the Mcp-Session-Id
/// header that every subsequent request must carry (McpLoopbackServer).
/// </summary>
public sealed class McpClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private int _requestCounter;

    /// <summary>Creates a client for the backend's published MCP descriptor.</summary>
    /// <param name="endpoint">Message endpoint (http://127.0.0.1:port/mcp).</param>
    /// <param name="token">Local MCP bearer token.</param>
    public McpClient(string endpoint, string token)
    {
        _endpoint = endpoint;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>Session id assigned by initialize (null before the handshake).</summary>
    public string? SessionId { get; private set; }

    /// <summary>Performs the initialize handshake and stores the session id.</summary>
    /// <param name="cancellationToken">Request token.</param>
    /// <returns>The initialize exchange (capabilities + serverInfo).</returns>
    public async Task<McpExchange> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var exchange = await RpcAsync("initialize", new
        {
            protocolVersion = "2025-06-18",
            capabilities = new { },
            clientInfo = new { name = "crawlforge-conformance-harness", version = "1.0.0" }
        }, cancellationToken);
        SessionId = exchange.SessionId;
        return exchange;
    }

    /// <summary>tools/list.</summary>
    /// <param name="cancellationToken">Request token.</param>
    /// <returns>The tools/list exchange.</returns>
    public Task<McpExchange> ListToolsAsync(CancellationToken cancellationToken = default) =>
        RpcAsync("tools/list", new { }, cancellationToken);

    /// <summary>resources/list.</summary>
    /// <param name="cancellationToken">Request token.</param>
    /// <returns>The resources/list exchange.</returns>
    public Task<McpExchange> ListResourcesAsync(CancellationToken cancellationToken = default) =>
        RpcAsync("resources/list", new { }, cancellationToken);

    /// <summary>prompts/list.</summary>
    /// <param name="cancellationToken">Request token.</param>
    /// <returns>The prompts/list exchange.</returns>
    public Task<McpExchange> ListPromptsAsync(CancellationToken cancellationToken = default) =>
        RpcAsync("prompts/list", new { }, cancellationToken);

    /// <summary>tools/call.</summary>
    /// <param name="name">Tool name.</param>
    /// <param name="arguments">Tool arguments.</param>
    /// <param name="cancellationToken">Request token.</param>
    /// <returns>The tools/call exchange.</returns>
    public Task<McpExchange> CallToolAsync(string name, object arguments, CancellationToken cancellationToken = default) =>
        RpcAsync("tools/call", new { name, arguments }, cancellationToken);

    /// <summary>resources/read.</summary>
    /// <param name="uri">Resource URI (crawlforge://...).</param>
    /// <param name="cancellationToken">Request token.</param>
    /// <returns>The resources/read exchange.</returns>
    public Task<McpExchange> ReadResourceAsync(string uri, CancellationToken cancellationToken = default) =>
        RpcAsync("resources/read", new { uri }, cancellationToken);

    /// <summary>prompts/get.</summary>
    /// <param name="name">Prompt name.</param>
    /// <param name="arguments">Prompt arguments (string map).</param>
    /// <param name="cancellationToken">Request token.</param>
    /// <returns>The prompts/get exchange.</returns>
    public Task<McpExchange> GetPromptAsync(string name, object arguments, CancellationToken cancellationToken = default) =>
        RpcAsync("prompts/get", new { name, arguments }, cancellationToken);

    /// <summary>Sends one raw JSON-RPC request (for error-shape conformance).</summary>
    /// <param name="requestJson">Raw request payload.</param>
    /// <param name="cancellationToken">Request token.</param>
    /// <returns>The exchange.</returns>
    public async Task<McpExchange> RawAsync(string requestJson, CancellationToken cancellationToken = default)
    {
        using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = content };
        if (SessionId is not null)
        {
            request.Headers.Add("Mcp-Session-Id", SessionId);
        }

        using var response = await _http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        var sessionId = response.Headers.TryGetValues("Mcp-Session-Id", out var values)
            ? values.FirstOrDefault()
            : null;
        JsonElement root = default;
        if (text.Length > 0)
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                root = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Non-JSON error bodies (401/400 plain text) stay default.
            }
        }

        return new McpExchange(requestJson, (int)response.StatusCode, text, root, sessionId);
    }

    private async Task<McpExchange> RpcAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _requestCounter);
        var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters });
        return await RawAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();
}
