using System.Text.Json;
using System.Text.Json.Nodes;

namespace CrawlForge.Mcp;

/// <summary>
/// MCP wire-protocol constants and JSON-RPC 2.0 helpers (FS-CRAWL-001 §11.1):
/// the hand-rolled, protocol-conformant surface over the locked
/// <c>IMcpToolSurface</c>/<c>IMcpResourceSurface</c>/<c>IMcpPromptSurface</c>.
/// No SDK dependency; conformance is proven by interop with the official
/// TypeScript SDK client (see the stdio adapter's interop suite).
/// </summary>
public static class McpProtocol
{
    /// <summary>Latest supported MCP protocol revision.</summary>
    public const string LatestProtocolVersion = "2025-06-18";

    /// <summary>Server name reported in the initialize handshake.</summary>
    public const string ServerName = "crawlforge";

    /// <summary>Server version reported in the initialize handshake.</summary>
    public const string ServerVersion = "1.0.0";

    /// <summary>Protocol revisions accepted during negotiation, newest first.</summary>
    public static readonly IReadOnlyList<string> SupportedProtocolVersions =
        ["2025-06-18", "2025-03-26", "2024-11-05"];

    /// <summary>JSON-RPC 2.0 parse error.</summary>
    public const int ParseError = -32700;

    /// <summary>JSON-RPC 2.0 invalid request.</summary>
    public const int InvalidRequest = -32600;

    /// <summary>JSON-RPC 2.0 method not found.</summary>
    public const int MethodNotFound = -32601;

    /// <summary>JSON-RPC 2.0 invalid params.</summary>
    public const int InvalidParams = -32602;

    /// <summary>JSON-RPC 2.0 internal error.</summary>
    public const int InternalError = -32603;

    /// <summary>Shared serializer options (camelCase, null-ignoring, string enums).</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>Negotiates the protocol version: echo the client's requested
    /// revision when supported, otherwise offer the latest supported one.</summary>
    /// <param name="requested">Client-requested protocol version, when any.</param>
    /// <returns>The negotiated protocol version.</returns>
    public static string NegotiateVersion(string? requested) =>
        requested is not null && SupportedProtocolVersions.Contains(requested)
            ? requested
            : LatestProtocolVersion;

    /// <summary>Builds a JSON-RPC 2.0 success response envelope.</summary>
    /// <param name="id">Request id (echoed verbatim).</param>
    /// <param name="result">Result payload.</param>
    /// <returns>Serialized response.</returns>
    public static string SuccessResponse(JsonElement? id, object result)
    {
        var envelope = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id is { } value ? JsonNode.Parse(value.GetRawText()) : null,
            ["result"] = JsonSerializer.SerializeToNode(result, JsonOptions),
        };
        return envelope.ToJsonString(JsonOptions);
    }

    /// <summary>Builds a JSON-RPC 2.0 error response envelope.</summary>
    /// <param name="id">Request id when known; null on parse errors.</param>
    /// <param name="code">JSON-RPC error code.</param>
    /// <param name="message">Human-readable error message.</param>
    /// <returns>Serialized response.</returns>
    public static string ErrorResponse(JsonElement? id, int code, string message)
    {
        var envelope = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id is { } value ? JsonNode.Parse(value.GetRawText()) : null,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        };
        return envelope.ToJsonString(JsonOptions);
    }
}
