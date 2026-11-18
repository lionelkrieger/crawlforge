using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrawlForge.ContractConformance.Tests.Harness;

/// <summary>Machine-readable cross-layer payload inventory (committed JSON).</summary>
public sealed class PayloadInventory
{
    /// <summary>Inventory schema version.</summary>
    public int Version { get; init; }

    /// <summary>Owning workpiece.</summary>
    public string Workpiece { get; init; } = "";

    /// <summary>Spec + code sources the inventory was derived from.</summary>
    public IReadOnlyDictionary<string, string> Sources { get; init; } = new Dictionary<string, string>();

    /// <summary>REST surface.</summary>
    public RestInventory Rest { get; init; } = new();

    /// <summary>Hub surface.</summary>
    public HubInventory Hub { get; init; } = new();

    /// <summary>IPC surface.</summary>
    public IpcInventory Ipc { get; init; } = new();

    /// <summary>MCP surface.</summary>
    public McpInventory Mcp { get; init; } = new();

    /// <summary>Storage surface.</summary>
    public StorageInventory Storage { get; init; } = new();

    /// <summary>Loads the committed inventory (output copy first, source fallback).</summary>
    /// <returns>The parsed inventory.</returns>
    public static PayloadInventory Load()
    {
        var outputCopy = Path.Combine(AppContext.BaseDirectory, "inventory", "payload-inventory.json");
        var path = File.Exists(outputCopy) ? outputCopy : RepoPaths.InventoryFile;
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<PayloadInventory>(json, JsonOptions)
            ?? throw new InvalidOperationException("Inventory deserialized to null.");
    }

    /// <summary>Every coverage reference in the inventory (test class.method names).</summary>
    /// <returns>All coveredBy entries flattened.</returns>
    public IEnumerable<string> AllCoverageReferences()
    {
        foreach (var operation in Rest.Operations)
        {
            foreach (var reference in operation.CoveredBy) yield return reference;
        }

        foreach (var hubEvent in Hub.Events)
        {
            foreach (var reference in hubEvent.CoveredBy) yield return reference;
        }

        foreach (var reference in Hub.ClientChannels.CoveredBy) yield return reference;

        foreach (var channel in Ipc.Channels)
        {
            foreach (var reference in channel.CoveredBy) yield return reference;
        }

        foreach (var tool in Mcp.Tools)
        {
            foreach (var reference in tool.CoveredBy) yield return reference;
        }

        foreach (var resource in Mcp.Resources)
        {
            foreach (var reference in resource.CoveredBy) yield return reference;
        }

        foreach (var prompt in Mcp.Prompts)
        {
            foreach (var reference in prompt.CoveredBy) yield return reference;
        }

        foreach (var entity in Storage.Entities)
        {
            foreach (var reference in entity.CoveredBy) yield return reference;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}

/// <summary>REST inventory section.</summary>
public sealed class RestInventory
{
    /// <summary>Expected route-group count (architecture Appendix B).</summary>
    public int ExpectedGroups { get; init; }

    /// <summary>Expected operation count (71 baseline + 37 additive).</summary>
    public int ExpectedOperations { get; init; }

    /// <summary>Catalog operations.</summary>
    public IReadOnlyList<RestOperationEntry> Operations { get; init; } = Array.Empty<RestOperationEntry>();
}

/// <summary>One inventoried REST operation.</summary>
public sealed class RestOperationEntry
{
    /// <summary>Catalog key.</summary>
    public string Key { get; init; } = "";

    /// <summary>HTTP method.</summary>
    public string Method { get; init; } = "";

    /// <summary>Route template.</summary>
    public string Path { get; init; } = "";

    /// <summary>Route group.</summary>
    public string Group { get; init; } = "";

    /// <summary>Additive flag.</summary>
    public bool Additive { get; init; }

    /// <summary>JSON-envelope flag (false = binary/text exemption).</summary>
    public bool Enveloped { get; init; }

    /// <summary>Conformance test coverage references.</summary>
    public IReadOnlyList<string> CoveredBy { get; init; } = Array.Empty<string>();
}

/// <summary>Hub inventory section.</summary>
public sealed class HubInventory
{
    /// <summary>Spec event count (18).</summary>
    public int SpecEvents { get; init; }

    /// <summary>Backend catalog event count (19).</summary>
    public int CatalogEvents { get; init; }

    /// <summary>Server events (spec + recorded drift).</summary>
    public IReadOnlyList<HubEventEntry> Events { get; init; } = Array.Empty<HubEventEntry>();

    /// <summary>Client→server channel methods.</summary>
    public HubChannelEntry ClientChannels { get; init; } = new();
}

/// <summary>One inventoried hub event.</summary>
public sealed class HubEventEntry
{
    /// <summary>Event name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Present in the spec event table.</summary>
    public bool InSpec { get; init; }

    /// <summary>Present in the frozen frontend contract.</summary>
    public bool InFrontend { get; init; }

    /// <summary>Present in the backend HubEventCatalog.</summary>
    public bool InCatalog { get; init; }

    /// <summary>Producer citation (or NONE).</summary>
    public string Producer { get; init; } = "";

    /// <summary>Whether the fixture scenario captures it live.</summary>
    public bool LiveCapture { get; init; }

    /// <summary>Conformance test coverage references.</summary>
    public IReadOnlyList<string> CoveredBy { get; init; } = Array.Empty<string>();

    /// <summary>Recorded gap id/reason, when any.</summary>
    public string? Gap { get; init; }
}

/// <summary>Client→server hub channel inventory.</summary>
public sealed class HubChannelEntry
{
    /// <summary>Conformance test coverage references.</summary>
    public IReadOnlyList<string> CoveredBy { get; init; } = Array.Empty<string>();

    /// <summary>Contract note.</summary>
    public string Note { get; init; } = "";
}

/// <summary>IPC inventory section.</summary>
public sealed class IpcInventory
{
    /// <summary>Frozen bridge channel count (9).</summary>
    public int ExpectedBridgeChannels { get; init; }

    /// <summary>Total channels including preload literals (11).</summary>
    public int ExpectedTotalChannels { get; init; }

    /// <summary>Channels.</summary>
    public IReadOnlyList<IpcChannelEntry> Channels { get; init; } = Array.Empty<IpcChannelEntry>();
}

/// <summary>One inventoried IPC channel.</summary>
public sealed class IpcChannelEntry
{
    /// <summary>Channel name (crawlforge:*).</summary>
    public string Channel { get; init; } = "";

    /// <summary>Direction.</summary>
    public string Direction { get; init; } = "";

    /// <summary>invoke / send / send/on.</summary>
    public string Kind { get; init; } = "";

    /// <summary>Source citation.</summary>
    public string Source { get; init; } = "";

    /// <summary>Conformance test coverage references.</summary>
    public IReadOnlyList<string> CoveredBy { get; init; } = Array.Empty<string>();
}

/// <summary>MCP inventory section.</summary>
public sealed class McpInventory
{
    /// <summary>Expected tool count (8).</summary>
    public int ExpectedTools { get; init; }

    /// <summary>Expected resource count (15).</summary>
    public int ExpectedResources { get; init; }

    /// <summary>Expected prompt count (5).</summary>
    public int ExpectedPrompts { get; init; }

    /// <summary>Tools.</summary>
    public IReadOnlyList<McpNamedEntry> Tools { get; init; } = Array.Empty<McpNamedEntry>();

    /// <summary>Resources.</summary>
    public IReadOnlyList<McpResourceEntry> Resources { get; init; } = Array.Empty<McpResourceEntry>();

    /// <summary>Prompts.</summary>
    public IReadOnlyList<McpNamedEntry> Prompts { get; init; } = Array.Empty<McpNamedEntry>();
}

/// <summary>One inventoried MCP tool or prompt.</summary>
public sealed class McpNamedEntry
{
    /// <summary>Tool/prompt name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Conformance test coverage references.</summary>
    public IReadOnlyList<string> CoveredBy { get; init; } = Array.Empty<string>();
}

/// <summary>One inventoried MCP resource.</summary>
public sealed class McpResourceEntry
{
    /// <summary>Resource URI template.</summary>
    public string Uri { get; init; } = "";

    /// <summary>Resource name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Conformance test coverage references.</summary>
    public IReadOnlyList<string> CoveredBy { get; init; } = Array.Empty<string>();

    /// <summary>Recorded gap (paging drift).</summary>
    public string? Gap { get; init; }
}

/// <summary>Storage inventory section.</summary>
public sealed class StorageInventory
{
    /// <summary>Expected entity count (40).</summary>
    public int ExpectedEntities { get; init; }

    /// <summary>Catalog entities.</summary>
    public IReadOnlyList<StorageEntityEntry> Entities { get; init; } = Array.Empty<StorageEntityEntry>();
}

/// <summary>One inventoried storage entity.</summary>
public sealed class StorageEntityEntry
{
    /// <summary>DTO type name.</summary>
    public string Entity { get; init; } = "";

    /// <summary>Catalog table name.</summary>
    public string Table { get; init; } = "";

    /// <summary>Conformance test coverage references.</summary>
    public IReadOnlyList<string> CoveredBy { get; init; } = Array.Empty<string>();
}
