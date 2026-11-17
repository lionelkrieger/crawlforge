using CrawlForge.Mcp;

namespace CrawlForge.Mcp.Tests;

/// <summary>
/// Composition helper: builds the full MCP surface stack over the real
/// persistence fixture with test doubles for the lifecycle services.
/// </summary>
public sealed class McpTestHost
{
    /// <summary>Creates the host over a prepared fixture.</summary>
    /// <param name="stack">Real persistence stack.</param>
    public McpTestHost(McpSqliteStackFixture stack)
    {
        Stack = stack;
        ActionLog = new AutomationActionLog(stack.ActionLog, stack.ActionLog);
        Execution = new FakeAuditExecutionService(stack.Projects, stack.Configurations, stack.Audits);
        Scheduler = new FakeScheduledAuditService();
        SinglePage = new FakeSinglePageAnalyzer();
        Tools = new McpToolSurface(Execution, stack.Lifecycle, Scheduler, SinglePage, stack.Audits);
        Resources = new McpResourceSurface(stack.BuildPorts());
        Prompts = new McpPromptSurface(Resources, stack.Comparison);
        Dispatcher = new McpJsonRpcDispatcher(Tools, Resources, Prompts, ActionLog);
        Tokens = new McpTokenService(stack.Protector, new FileMcpTokenStore(
            Path.Combine(Path.GetTempPath(), "cf-mcp-token-" + Guid.NewGuid().ToString("N") + ".tok")));
    }

    /// <summary>Persistence stack.</summary>
    public McpSqliteStackFixture Stack { get; }

    /// <summary>Action log under test.</summary>
    public AutomationActionLog ActionLog { get; }

    /// <summary>Execution double.</summary>
    public FakeAuditExecutionService Execution { get; }

    /// <summary>Scheduler double.</summary>
    public FakeScheduledAuditService Scheduler { get; }

    /// <summary>Single-page double.</summary>
    public FakeSinglePageAnalyzer SinglePage { get; }

    /// <summary>Tool surface.</summary>
    public McpToolSurface Tools { get; }

    /// <summary>Resource surface.</summary>
    public McpResourceSurface Resources { get; }

    /// <summary>Prompt surface.</summary>
    public McpPromptSurface Prompts { get; }

    /// <summary>Dispatcher.</summary>
    public McpJsonRpcDispatcher Dispatcher { get; }

    /// <summary>Token service.</summary>
    public McpTokenService Tokens { get; }
}
