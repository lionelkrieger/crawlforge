using System.Reflection;
using System.Text.RegularExpressions;
using CrawlForge.Api.Hubs;
using CrawlForge.ContractConformance.Tests.Harness;
using CrawlForge.Data.Repositories;
using Xunit;

namespace CrawlForge.ContractConformance.Tests;

/// <summary>
/// Inventory conformance (WP-08-03): the committed machine-readable payload
/// inventory must match the frozen sources it was derived from, and every
/// inventoried payload must carry at least one conformance-test reference that
/// resolves to a real test method in this assembly.
/// </summary>
public sealed class PayloadInventoryTests
{
    private static readonly PayloadInventory Inventory = PayloadInventory.Load();

    /// <summary>REST inventory matches the frozen frontend catalog exactly (108 ops / 88 groups).</summary>
    [Fact]
    public void RestCatalog_Has88GroupsAnd108Operations()
    {
        var frozen = RouteCatalog.ParseFrozenCatalog();
        Assert.Equal(108, frozen.Count);
        Assert.Equal(88, frozen.Select(o => o.Group).Distinct().Count());
        Assert.Equal(Inventory.Rest.ExpectedOperations, frozen.Count);
        Assert.Equal(Inventory.Rest.ExpectedGroups, frozen.Select(o => o.Group).Distinct().Count());

        Assert.Equal(frozen.Count, Inventory.Rest.Operations.Count);
        foreach (var (expected, actual) in frozen.Zip(Inventory.Rest.Operations))
        {
            Assert.Equal(expected.Key, actual.Key);
            Assert.Equal(expected.Method, actual.Method);
            Assert.Equal(expected.Path, actual.Path);
            Assert.Equal(expected.Group, actual.Group);
            Assert.Equal(expected.Additive, actual.Additive);
            Assert.Equal(expected.Enveloped, actual.Enveloped);
        }
    }

    /// <summary>
    /// Every frozen catalog route exists on the live backend's controllers
    /// (reflection cross-check; additive routes beyond the catalog are recorded,
    /// not failed — the catalog is the consumer contract floor).
    /// </summary>
    [Fact]
    public void RestCatalog_EveryRouteExistsOnBackendControllers()
    {
        var backendRoutes = RouteCatalog.DiscoverBackendRoutes()
            .Select(r => (r.Method, Template: RouteCatalog.Normalize(r.Template)))
            .ToHashSet();
        var missing = RouteCatalog.ParseFrozenCatalog()
            .Where(operation => !backendRoutes.Contains((operation.Method, RouteCatalog.Normalize(operation.Path))))
            .Select(operation => $"{operation.Method} {operation.Path} ({operation.Key})")
            .ToArray();
        Assert.Empty(missing);
    }

    /// <summary>Hub inventory matches the spec surface, the frontend contract, and the backend catalog.</summary>
    [Fact]
    public void HubCatalog_MatchesSpecFrontendAndBackend()
    {
        // Frontend frozen server event names (HUB_SERVER_EVENT_NAMES array).
        var eventsTs = File.ReadAllText(RepoPaths.FrontendHubEventsTs);
        var arrayMatch = Regex.Match(eventsTs, "HUB_SERVER_EVENT_NAMES[^=]*=\\s*\\[(?<body>[^\\]]*)\\]", RegexOptions.Singleline);
        Assert.True(arrayMatch.Success, "HUB_SERVER_EVENT_NAMES not found in events.ts");
        var frontendNames = Regex.Matches(arrayMatch.Groups["body"].Value, "'([^']+)'")
            .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        // CR-027 + G-I (WP-09-11): local-notification and export-progress joined
        // the frozen frontend contract (18 → 20 server events).
        Assert.Equal(20, frontendNames.Count);

        // Backend catalog constants minus the two client→server channel methods.
        var catalogNames = typeof(HubEventCatalog)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Where(name => name is not ("subscribe" or "unsubscribe"))
            .ToHashSet(StringComparer.Ordinal);
        // G-I (WP-09-11 5ad460f): ExportProgress cataloged (19 → 20).
        Assert.Equal(20, catalogNames.Count);

        Assert.Equal(Inventory.Hub.SpecEvents, frontendNames.Count);
        Assert.Equal(Inventory.Hub.CatalogEvents, catalogNames.Count);

        var inventoried = Inventory.Hub.Events.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        // Spec events ⊆ inventory; catalog events ⊆ inventory. The two formerly
        // drifted events (local-notification, export-progress) are now cataloged
        // AND in the frozen frontend contract (CR-027 + G-I, WP-09-11), so the
        // inventory set equals the catalog set exactly.
        Assert.True(frontendNames.IsSubsetOf(inventoried), "spec/frontend events missing from inventory");
        Assert.True(catalogNames.IsSubsetOf(inventoried), "catalog events missing from inventory");
        Assert.Contains("local-notification", inventoried);
        Assert.Contains("export-progress", inventoried);
        Assert.Equal(catalogNames.Count, inventoried.Count);

        foreach (var entry in Inventory.Hub.Events)
        {
            Assert.Equal(frontendNames.Contains(entry.Name), entry.InFrontend);
            Assert.Equal(catalogNames.Contains(entry.Name), entry.InCatalog);
            Assert.Equal(frontendNames.Contains(entry.Name), entry.InSpec);
        }
    }

    /// <summary>IPC inventory matches the frozen bridge channels plus the two preload literals.</summary>
    [Fact]
    public void IpcCatalog_MatchesFrozenBridgeAndPreload()
    {
        var bridgeTs = File.ReadAllText(RepoPaths.ElectronBridgeTs);
        var blockMatch = Regex.Match(bridgeTs, "IPC_CHANNELS\\s*=\\s*Object\\.freeze\\(\\{(?<body>[^}]*)\\}", RegexOptions.Singleline);
        Assert.True(blockMatch.Success, "IPC_CHANNELS block not found in bridge.ts");
        var bridgeChannels = Regex.Matches(blockMatch.Groups["body"].Value, "'(crawlforge:[^']+)'")
            .Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(9, bridgeChannels.Length);

        var preloadTs = File.ReadAllText(RepoPaths.ElectronPreloadTs);
        Assert.Contains("'crawlforge:get-bridge'", preloadTs, StringComparison.Ordinal);
        Assert.Contains("'crawlforge:hydrated'", preloadTs, StringComparison.Ordinal);

        Assert.Equal(Inventory.Ipc.ExpectedBridgeChannels, bridgeChannels.Length);
        Assert.Equal(Inventory.Ipc.ExpectedTotalChannels, Inventory.Ipc.Channels.Count);
        var inventoried = Inventory.Ipc.Channels.Select(c => c.Channel).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(11, inventoried.Count);
        foreach (var channel in bridgeChannels)
        {
            Assert.Contains(channel, inventoried);
        }

        Assert.Contains("crawlforge:get-bridge", inventoried);
        Assert.Contains("crawlforge:hydrated", inventoried);
        Assert.All(Inventory.Ipc.Channels, c => Assert.StartsWith("crawlforge:", c.Channel, StringComparison.Ordinal));
    }

    /// <summary>Storage inventory matches SchemaNames.AllEntities (40 catalog entities).</summary>
    [Fact]
    public void StorageCatalog_MatchesSchemaNames()
    {
        var entities = SchemaNames.AllEntities;
        Assert.Equal(40, entities.Count);
        Assert.Equal(Inventory.Storage.ExpectedEntities, entities.Count);
        Assert.Equal(entities.Count, Inventory.Storage.Entities.Count);
        foreach (var (type, entry) in entities.Zip(Inventory.Storage.Entities))
        {
            Assert.Equal(type.Name, entry.Entity);
            Assert.Equal(SchemaNames.TableName(type), entry.Table);
        }
    }

    /// <summary>MCP inventory counts match the locked surface (8 tools / 15 resources / 5 prompts).</summary>
    [Fact]
    public void McpCatalog_HasLockedSurfaceCounts()
    {
        Assert.Equal(8, Inventory.Mcp.ExpectedTools);
        Assert.Equal(15, Inventory.Mcp.ExpectedResources);
        Assert.Equal(5, Inventory.Mcp.ExpectedPrompts);
        Assert.Equal(Inventory.Mcp.ExpectedTools, Inventory.Mcp.Tools.Count);
        Assert.Equal(Inventory.Mcp.ExpectedResources, Inventory.Mcp.Resources.Count);
        Assert.Equal(Inventory.Mcp.ExpectedPrompts, Inventory.Mcp.Prompts.Count);
        Assert.All(Inventory.Mcp.Resources, r => Assert.StartsWith("crawlforge://", r.Uri, StringComparison.Ordinal));
    }

    /// <summary>
    /// Every inventoried payload carries coverage, and every coverage reference
    /// resolves to a real [Fact]/[Theory] method in this assembly (skipped tests
    /// count as coverage only when they carry a documented gap reason).
    /// </summary>
    [Fact]
    public void EveryInventoriedPayload_HasConformanceCoverage()
    {
        var testMethods = typeof(PayloadInventoryTests).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttributes().Any(a => a.GetType().Name is "FactAttribute" or "TheoryAttribute"))
            .Select(m => m.DeclaringType!.Name + "." + m.Name)
            .ToHashSet(StringComparer.Ordinal);

        var sections = new (string Name, IEnumerable<string> Refs, int Count)[]
        {
            ("rest", Inventory.Rest.Operations.SelectMany(o => o.CoveredBy), Inventory.Rest.Operations.Count),
            ("hub", Inventory.Hub.Events.SelectMany(e => e.CoveredBy).Concat(Inventory.Hub.ClientChannels.CoveredBy), Inventory.Hub.Events.Count),
            ("ipc", Inventory.Ipc.Channels.SelectMany(c => c.CoveredBy), Inventory.Ipc.Channels.Count),
            ("mcp.tools", Inventory.Mcp.Tools.SelectMany(t => t.CoveredBy), Inventory.Mcp.Tools.Count),
            ("mcp.resources", Inventory.Mcp.Resources.SelectMany(r => r.CoveredBy), Inventory.Mcp.Resources.Count),
            ("mcp.prompts", Inventory.Mcp.Prompts.SelectMany(p => p.CoveredBy), Inventory.Mcp.Prompts.Count),
            ("storage", Inventory.Storage.Entities.SelectMany(e => e.CoveredBy), Inventory.Storage.Entities.Count)
        };

        var problems = new List<string>();
        foreach (var (name, refs, count) in sections)
        {
            Assert.True(count > 0, $"{name} inventory section is empty");
            foreach (var reference in refs)
            {
                if (!testMethods.Contains(reference))
                {
                    problems.Add($"{name}: coverage reference '{reference}' does not resolve to a test method");
                }
            }
        }

        // Per-entry non-empty coverage.
        foreach (var operation in Inventory.Rest.Operations)
        {
            if (operation.CoveredBy.Count == 0) problems.Add($"rest:{operation.Key} has no coverage");
        }

        foreach (var hubEvent in Inventory.Hub.Events)
        {
            if (hubEvent.CoveredBy.Count == 0) problems.Add($"hub:{hubEvent.Name} has no coverage");
        }

        foreach (var channel in Inventory.Ipc.Channels)
        {
            if (channel.CoveredBy.Count == 0) problems.Add($"ipc:{channel.Channel} has no coverage");
        }

        foreach (var tool in Inventory.Mcp.Tools)
        {
            if (tool.CoveredBy.Count == 0) problems.Add($"mcp.tool:{tool.Name} has no coverage");
        }

        foreach (var resource in Inventory.Mcp.Resources)
        {
            if (resource.CoveredBy.Count == 0) problems.Add($"mcp.resource:{resource.Name} has no coverage");
        }

        foreach (var prompt in Inventory.Mcp.Prompts)
        {
            if (prompt.CoveredBy.Count == 0) problems.Add($"mcp.prompt:{prompt.Name} has no coverage");
        }

        foreach (var entity in Inventory.Storage.Entities)
        {
            if (entity.CoveredBy.Count == 0) problems.Add($"storage:{entity.Entity} has no coverage");
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
