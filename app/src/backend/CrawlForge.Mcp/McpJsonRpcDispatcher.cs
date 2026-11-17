using System.Text.Json;
using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Mcp;

/// <summary>
/// Result of dispatching one JSON-RPC message.
/// </summary>
/// <param name="ResponseJson">Response envelope; null for notifications.</param>
/// <param name="SessionId">Session id assigned on initialize; null otherwise.</param>
public sealed record McpDispatchResult(string? ResponseJson, string? SessionId);

/// <summary>
/// Hand-rolled JSON-RPC 2.0 dispatcher implementing the MCP server semantics
/// (FS-CRAWL-001 §11.1) over the locked tool/resource/prompt surfaces:
/// capability negotiation, tool invocation, resource reading, and prompt
/// materialization. Every request is recorded in the automation action log
/// (§11.5, criterion #23). The dispatcher holds no transport state; the
/// loopback server and the stdio adapter own session enforcement.
/// </summary>
public sealed class McpJsonRpcDispatcher
{
    private readonly IMcpToolSurface _tools;
    private readonly IMcpResourceSurface _resources;
    private readonly IMcpPromptSurface _prompts;
    private readonly IAutomationActionLog _actionLog;

    /// <summary>Creates the dispatcher.</summary>
    /// <param name="tools">Locked tool surface.</param>
    /// <param name="resources">Locked resource surface.</param>
    /// <param name="prompts">Locked prompt surface.</param>
    /// <param name="actionLog">Automation action log.</param>
    public McpJsonRpcDispatcher(
        IMcpToolSurface tools,
        IMcpResourceSurface resources,
        IMcpPromptSurface prompts,
        IAutomationActionLog actionLog)
    {
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _prompts = prompts ?? throw new ArgumentNullException(nameof(prompts));
        _actionLog = actionLog ?? throw new ArgumentNullException(nameof(actionLog));
    }

    /// <summary>Dispatches one JSON-RPC 2.0 message.</summary>
    /// <param name="requestJson">Raw request payload.</param>
    /// <param name="transport">Transport the message arrived on.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The dispatch result (null response for notifications).</returns>
    public async Task<McpDispatchResult> DispatchAsync(
        string requestJson,
        AutomationTransportKind transport,
        CancellationToken cancellationToken = default)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(requestJson);
        }
        catch (JsonException)
        {
            return new McpDispatchResult(McpProtocol.ErrorResponse(null, McpProtocol.ParseError, "Parse error"), null);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new McpDispatchResult(
                    McpProtocol.ErrorResponse(null, McpProtocol.InvalidRequest, "Invalid request"), null);
            }

            JsonElement? id = root.TryGetProperty("id", out var idElement) ? idElement : null;
            var method = root.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String
                ? methodElement.GetString()
                : null;
            var hasParams = root.TryGetProperty("params", out var paramsElement);

            if (method is null)
            {
                return new McpDispatchResult(
                    McpProtocol.ErrorResponse(id, McpProtocol.InvalidRequest, "Invalid request: missing method"), null);
            }

            // Notifications (no id) never produce a response and are not invocations.
            if (id is null)
            {
                return new McpDispatchResult(null, null);
            }

            var outcome = AutomationOutcome.Success;
            string response;
            string? sessionId = null;
            try
            {
                (response, sessionId) = await RouteAsync(method, id, hasParams ? paramsElement : null, cancellationToken)
                    .ConfigureAwait(false);

                // Error envelopes (e.g. method-not-found) are failed invocations.
                using var responseDocument = JsonDocument.Parse(response);
                if (responseDocument.RootElement.TryGetProperty("error", out _))
                {
                    outcome = AutomationOutcome.Failed;
                }
                else if (method == "tools/call"
                    && responseDocument.RootElement.TryGetProperty("result", out var resultElement)
                    && resultElement.ValueKind == JsonValueKind.Object
                    && resultElement.TryGetProperty("isError", out var isErrorElement)
                    && isErrorElement.ValueKind == JsonValueKind.True)
                {
                    // §11.5 outcome semantics (D7): tool-level failures arrive
                    // as result.isError=true inside a SUCCESS JSON-RPC envelope
                    // (the tool surface catches its own exceptions) — an errored
                    // invocation is Failed, not Success.
                    outcome = AutomationOutcome.Failed;
                }
            }
            catch (McpArgumentMissingException ex)
            {
                outcome = AutomationOutcome.Failed;
                response = McpProtocol.ErrorResponse(id, McpProtocol.InvalidParams, ex.Message);
            }
            catch (McpResourceNotFoundException ex)
            {
                outcome = AutomationOutcome.Failed;
                response = McpProtocol.ErrorResponse(id, McpProtocol.InvalidParams, ex.Message);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                outcome = AutomationOutcome.Failed;
                response = McpProtocol.ErrorResponse(id, McpProtocol.InternalError, ex.Message);
            }

            var summaryArguments = ExtractArguments(hasParams ? paramsElement : null);
            if (method == "tools/call"
                && hasParams
                && paramsElement.TryGetProperty("name", out var toolNameElement)
                && toolNameElement.ValueKind == JsonValueKind.String)
            {
                summaryArguments = new Dictionary<string, object?>(summaryArguments)
                {
                    ["tool"] = toolNameElement.GetString(),
                };
            }

            await _actionLog.LogAsync(new AutomationActionEntry(
                transport,
                method,
                McpArguments.Summarize(summaryArguments),
                outcome), cancellationToken).ConfigureAwait(false);

            return new McpDispatchResult(response, sessionId);
        }
    }

    private async Task<(string Response, string? SessionId)> RouteAsync(
        string method,
        JsonElement? id,
        JsonElement? parameters,
        CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "initialize":
            {
                var requested = parameters is { } p
                    && p.TryGetProperty("protocolVersion", out var version)
                    && version.ValueKind == JsonValueKind.String
                    ? version.GetString()
                    : null;
                var sessionId = Guid.NewGuid().ToString("N");
                var result = new Dictionary<string, object?>
                {
                    ["protocolVersion"] = McpProtocol.NegotiateVersion(requested),
                    ["capabilities"] = new Dictionary<string, object?>
                    {
                        ["tools"] = new Dictionary<string, object?> { ["listChanged"] = false },
                        ["resources"] = new Dictionary<string, object?> { ["subscribe"] = false, ["listChanged"] = false },
                        ["prompts"] = new Dictionary<string, object?> { ["listChanged"] = false },
                    },
                    ["serverInfo"] = new Dictionary<string, object?>
                    {
                        ["name"] = McpProtocol.ServerName,
                        ["version"] = McpProtocol.ServerVersion,
                    },
                    ["instructions"] = "CrawlForge local automation interface. Loopback-only; no settings, credential, or configuration management is exposed.",
                };
                return (McpProtocol.SuccessResponse(id, result), sessionId);
            }

            case "ping":
                return (McpProtocol.SuccessResponse(id, new Dictionary<string, object?>()), null);

            case "tools/list":
            {
                var tools = await _tools.ListToolsAsync(cancellationToken).ConfigureAwait(false);
                var entries = tools.Select(tool => new Dictionary<string, object?>
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["inputSchema"] = JsonSerializer.Deserialize<JsonElement>(tool.InputSchemaJson),
                }).ToArray();
                return (McpProtocol.SuccessResponse(id, new Dictionary<string, object?> { ["tools"] = entries }), null);
            }

            case "tools/call":
            {
                var name = RequireStringProperty(parameters, "name");
                var arguments = ExtractArguments(parameters);
                var result = await _tools.InvokeToolAsync(name, arguments, cancellationToken).ConfigureAwait(false);
                var payload = new Dictionary<string, object?>
                {
                    ["content"] = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "text",
                            ["text"] = result.IsSuccess ? result.ContentJson : result.ErrorMessage ?? "Tool invocation failed.",
                        },
                    },
                    ["isError"] = !result.IsSuccess,
                };
                return (McpProtocol.SuccessResponse(id, payload), null);
            }

            case "resources/list":
            {
                var resources = await _resources.ListResourcesAsync(cancellationToken).ConfigureAwait(false);
                var entries = resources.Select(resource => new Dictionary<string, object?>
                {
                    ["uri"] = resource.Uri,
                    ["name"] = resource.Name,
                    ["description"] = resource.Description,
                    ["mimeType"] = resource.MimeType,
                }).ToArray();
                return (McpProtocol.SuccessResponse(id, new Dictionary<string, object?> { ["resources"] = entries }), null);
            }

            case "resources/read":
            {
                var uri = RequireStringProperty(parameters, "uri");
                var content = await _resources.ReadResourceAsync(uri, cancellationToken).ConfigureAwait(false);
                var payload = new Dictionary<string, object?>
                {
                    ["contents"] = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["uri"] = content.Uri,
                            ["mimeType"] = content.MimeType,
                            ["text"] = content.Text,
                        },
                    },
                };
                return (McpProtocol.SuccessResponse(id, payload), null);
            }

            case "prompts/list":
            {
                var prompts = await _prompts.ListPromptsAsync(cancellationToken).ConfigureAwait(false);
                var entries = prompts.Select(prompt => new Dictionary<string, object?>
                {
                    ["name"] = prompt.Name,
                    ["description"] = prompt.Description,
                    ["arguments"] = prompt.Arguments.Select(argument => new Dictionary<string, object?>
                    {
                        ["name"] = argument.Name,
                        ["description"] = argument.Description,
                        ["required"] = argument.Required,
                    }).ToArray(),
                }).ToArray();
                return (McpProtocol.SuccessResponse(id, new Dictionary<string, object?> { ["prompts"] = entries }), null);
            }

            case "prompts/get":
            {
                var name = RequireStringProperty(parameters, "name");
                var arguments = ExtractStringArguments(parameters);
                var content = await _prompts.GetPromptAsync(name, arguments, cancellationToken).ConfigureAwait(false);
                var payload = new Dictionary<string, object?>
                {
                    ["messages"] = content.Messages.Select(message => new Dictionary<string, object?>
                    {
                        ["role"] = message.Role,
                        ["content"] = new Dictionary<string, object?> { ["type"] = "text", ["text"] = message.Text },
                    }).ToArray(),
                };
                return (McpProtocol.SuccessResponse(id, payload), null);
            }

            default:
                return (McpProtocol.ErrorResponse(id, McpProtocol.MethodNotFound, $"Method not found: {method}"), null);
        }
    }

    private static string RequireStringProperty(JsonElement? parameters, string name)
    {
        if (parameters is { } p
            && p.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() is { } text)
        {
            return text;
        }

        throw new McpArgumentMissingException(name);
    }

    private static IReadOnlyDictionary<string, object?> ExtractArguments(JsonElement? parameters)
    {
        var arguments = new Dictionary<string, object?>();
        if (parameters is { } p
            && p.TryGetProperty("arguments", out var args)
            && args.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in args.EnumerateObject())
            {
                arguments[property.Name] = property.Value;
            }
        }

        return arguments;
    }

    private static IReadOnlyDictionary<string, string>? ExtractStringArguments(JsonElement? parameters)
    {
        if (parameters is not { } p
            || !p.TryGetProperty("arguments", out var args)
            || args.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var arguments = new Dictionary<string, string>();
        foreach (var property in args.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { } text)
            {
                arguments[property.Name] = text;
            }
        }

        return arguments;
    }
}
