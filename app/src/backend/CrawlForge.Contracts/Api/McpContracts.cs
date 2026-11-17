namespace CrawlForge.Contracts.Api;

/// <summary>
/// Local MCP tool surface (FS-CRAWL-001 §11): the loopback-only automation
/// interface with locally generated credentials. Tools are read/diagnostic;
/// the surface never consults a remote control plane (exclusion row 4).
/// </summary>
public interface IMcpToolSurface
{
    /// <summary>Lists the tools exposed by the local automation interface.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Tool definitions.</returns>
    Task<IReadOnlyList<McpToolDefinition>> ListToolsAsync(CancellationToken cancellationToken = default);

    /// <summary>Invokes a tool with arguments.</summary>
    /// <param name="toolName">Tool name.</param>
    /// <param name="arguments">Tool arguments.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Invocation result.</returns>
    Task<McpInvocationResult> InvokeToolAsync(string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default);
}

/// <summary>
/// One MCP tool definition.
/// </summary>
/// <param name="Name">Tool name.</param>
/// <param name="Description">Human-readable description.</param>
/// <param name="InputSchemaJson">JSON schema of the tool input.</param>
public sealed record McpToolDefinition(string Name, string Description, string InputSchemaJson);

/// <summary>
/// Result of an MCP tool invocation.
/// </summary>
/// <param name="IsSuccess">Whether the invocation succeeded.</param>
/// <param name="ContentJson">Result content payload.</param>
/// <param name="ErrorMessage">Error message on failure.</param>
public sealed record McpInvocationResult(bool IsSuccess, string ContentJson, string? ErrorMessage);

/// <summary>
/// Local MCP resource surface (FS-CRAWL-001 §11.4): read-only resources over
/// audit data, including the additive resources (waf, host telemetry, ngrams,
/// redirect matrix, logs, semantics, governance, analytics).
/// </summary>
public interface IMcpResourceSurface
{
    /// <summary>Lists the resources exposed by the local automation interface.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Resource definitions.</returns>
    Task<IReadOnlyList<McpResourceDefinition>> ListResourcesAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads a resource by URI.</summary>
    /// <param name="uri">Resource URI.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Resource content.</returns>
    Task<McpResourceContent> ReadResourceAsync(string uri, CancellationToken cancellationToken = default);
}

/// <summary>
/// One MCP resource definition.
/// </summary>
/// <param name="Uri">Resource URI.</param>
/// <param name="Name">Resource name.</param>
/// <param name="Description">Optional description.</param>
/// <param name="MimeType">Optional MIME type of the content.</param>
public sealed record McpResourceDefinition(string Uri, string Name, string? Description, string? MimeType);

/// <summary>
/// Content of one MCP resource.
/// </summary>
/// <param name="Uri">Resource URI.</param>
/// <param name="MimeType">MIME type of the content.</param>
/// <param name="Text">Text content.</param>
public sealed record McpResourceContent(string Uri, string MimeType, string Text);

/// <summary>
/// Local MCP prompt surface (FS-CRAWL-001 §11): reusable prompt templates
/// served locally.
/// </summary>
public interface IMcpPromptSurface
{
    /// <summary>Lists the prompts exposed by the local automation interface.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Prompt definitions.</returns>
    Task<IReadOnlyList<McpPromptDefinition>> ListPromptsAsync(CancellationToken cancellationToken = default);

    /// <summary>Materializes a prompt with arguments.</summary>
    /// <param name="name">Prompt name.</param>
    /// <param name="arguments">Optional prompt arguments.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Prompt messages.</returns>
    Task<McpPromptContent> GetPromptAsync(string name, IReadOnlyDictionary<string, string>? arguments, CancellationToken cancellationToken = default);
}

/// <summary>
/// One MCP prompt definition.
/// </summary>
/// <param name="Name">Prompt name.</param>
/// <param name="Description">Optional description.</param>
/// <param name="Arguments">Declared prompt arguments.</param>
public sealed record McpPromptDefinition(string Name, string? Description, IReadOnlyList<McpPromptArgument> Arguments);

/// <summary>
/// One declared prompt argument.
/// </summary>
/// <param name="Name">Argument name.</param>
/// <param name="Description">Optional description.</param>
/// <param name="Required">Whether the argument is required.</param>
public sealed record McpPromptArgument(string Name, string? Description, bool Required);

/// <summary>
/// Materialized prompt content.
/// </summary>
/// <param name="Name">Prompt name.</param>
/// <param name="Messages">Prompt messages in order.</param>
public sealed record McpPromptContent(string Name, IReadOnlyList<McpPromptMessage> Messages);

/// <summary>
/// One prompt message.
/// </summary>
/// <param name="Role">Message role.</param>
/// <param name="Text">Message text.</param>
public sealed record McpPromptMessage(string Role, string Text);
