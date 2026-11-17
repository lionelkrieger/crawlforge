using System.Globalization;
using System.Text.Json;
using CrawlForge.Audit.Scoring;
using CrawlForge.Contracts.Api;

namespace CrawlForge.Mcp;

/// <summary>
/// MCP prompt surface (FS-CRAWL-001 §11 + Appendix D): five reusable,
/// side-effect-free briefing templates composed from the read-only resource
/// surface and the accepted cross-audit comparison reader. Prompt
/// materialization never mutates state and never fetches remote content.
/// </summary>
public sealed class McpPromptSurface : IMcpPromptSurface
{
    private readonly McpResourceSurface _resources;
    private readonly AuditComparisonReader _comparison;

    /// <summary>Creates the prompt surface.</summary>
    /// <param name="resources">Read-only resource surface used for composition.</param>
    /// <param name="comparison">Accepted cross-audit comparison reader.</param>
    public McpPromptSurface(McpResourceSurface resources, AuditComparisonReader comparison)
    {
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _comparison = comparison ?? throw new ArgumentNullException(nameof(comparison));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<McpPromptDefinition>> ListPromptsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Definitions);

    /// <inheritdoc/>
    public async Task<McpPromptContent> GetPromptAsync(
        string name,
        IReadOnlyDictionary<string, string>? arguments,
        CancellationToken cancellationToken = default)
    {
        var args = arguments ?? new Dictionary<string, string>();
        string text;
        switch (name)
        {
            case "audit-health-briefing":
            {
                var auditId = RequireAuditId(args, "auditId");
                var summary = await ReadResourceAsync($"crawlforge://audits/{auditId}/summary", cancellationToken).ConfigureAwait(false);
                text = "You are reviewing the health of a CrawlForge website audit. "
                    + "Summarize the overall state, category scores, and the most important issues, "
                    + "then recommend the next three actions.\n\nAudit summary (JSON):\n" + summary;
                break;
            }

            case "high-severity-triage":
            {
                var auditId = RequireAuditId(args, "auditId");
                var findings = await ReadResourceAsync($"crawlforge://audits/{auditId}/high-severity-findings", cancellationToken).ConfigureAwait(false);
                text = "You are triaging high-severity findings from a CrawlForge audit. "
                    + "Group the findings by root cause, rank them by likely impact, and propose a concrete fix for each group. "
                    + "Use the evidence payloads; do not invent data.\n\nHigh-severity findings (JSON):\n" + findings;
                break;
            }

            case "audit-delta-briefing":
            {
                var baseAuditId = RequireAuditId(args, "baseAuditId");
                var targetAuditId = RequireAuditId(args, "targetAuditId");
                var comparison = await _comparison.CompareAsync(baseAuditId, targetAuditId, cancellationToken).ConfigureAwait(false);
                text = "You are briefing a user on what changed between two CrawlForge audits of the same project. "
                    + "Highlight new and resolved findings, metric deltas, and score movement; call out regressions first.\n\n"
                    + "Comparison (JSON):\n" + JsonSerializer.Serialize(comparison, McpProtocol.JsonOptions);
                break;
            }

            case "ngram-topical-briefing":
            {
                var auditId = RequireAuditId(args, "auditId");
                var ngrams = await ReadResourceAsync($"crawlforge://audits/{auditId}/ngrams", cancellationToken).ConfigureAwait(false);
                var summary = await ReadResourceAsync($"crawlforge://audits/{auditId}/summary", cancellationToken).ConfigureAwait(false);
                text = "You are composing a topical briefing from a CrawlForge audit's n-gram analysis. "
                    + "Identify the dominant topics, keyword cannibalization risks, and content gaps, "
                    + "and relate them to the audit's overall health.\n\nN-gram analysis (JSON):\n" + ngrams
                    + "\n\nAudit summary (JSON):\n" + summary;
                break;
            }

            case "semantic-topical-briefing":
            {
                var auditId = RequireAuditId(args, "auditId");
                var semantics = await ReadResourceAsync($"crawlforge://audits/{auditId}/semantic-links", cancellationToken).ConfigureAwait(false);
                var summary = await ReadResourceAsync($"crawlforge://audits/{auditId}/summary", cancellationToken).ConfigureAwait(false);
                text = "You are composing a semantic topical briefing from a CrawlForge audit. "
                    + "Summarize the internal-link opportunities and semantic cannibalization clusters; "
                    + "remember these are suggestions only and must never be applied automatically.\n\n"
                    + "Semantic link analysis (JSON):\n" + semantics
                    + "\n\nAudit summary (JSON):\n" + summary;
                break;
            }

            default:
                throw new McpArgumentMissingException($"prompt '{name}'");
        }

        return new McpPromptContent(name, [new McpPromptMessage("user", text)]);
    }

    private static readonly IReadOnlyList<McpPromptDefinition> Definitions =
    [
        new("audit-health-briefing", "Composes the audit summary into an agent-ready health briefing; no side effects.",
            [new McpPromptArgument("auditId", "Audit identifier", true)]),
        new("high-severity-triage", "Composes high-severity findings into a triage briefing; no side effects.",
            [new McpPromptArgument("auditId", "Audit identifier", true)]),
        new("audit-delta-briefing", "Composes a cross-audit comparison into a delta briefing; no side effects.",
            [new McpPromptArgument("baseAuditId", "Earlier audit identifier", true),
             new McpPromptArgument("targetAuditId", "Later audit identifier", true)]),
        new("ngram-topical-briefing", "Composes ngram-analysis + audit-summary into a topical briefing; no side effects.",
            [new McpPromptArgument("auditId", "Audit identifier", true)]),
        new("semantic-topical-briefing", "Composes semantic-link-opportunities + audit-summary into a topical briefing; no side effects.",
            [new McpPromptArgument("auditId", "Audit identifier", true)]),
    ];

    private static long RequireAuditId(IReadOnlyDictionary<string, string> arguments, string name)
    {
        if (arguments.TryGetValue(name, out var raw)
            && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        throw new McpArgumentMissingException(name);
    }

    private async Task<string> ReadResourceAsync(string uri, CancellationToken cancellationToken)
    {
        var content = await _resources.ReadResourceAsync(uri, cancellationToken).ConfigureAwait(false);
        return content.Text;
    }
}
