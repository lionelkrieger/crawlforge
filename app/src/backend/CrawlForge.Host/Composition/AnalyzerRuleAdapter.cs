using CrawlForge.Contracts.Audit;

namespace CrawlForge.Host.Composition;

/// <summary>
/// Adapts one member of the accepted analyzer family onto the generic
/// <see cref="IAuditRuleAnalyzer"/> rule-host surface (WP-06-01 composition).
/// The analyzer interfaces share the AnalyzeAsync(context) shape; this adapter
/// contributes the family member's rule keys and findings to the rule host
/// without modifying any accepted analyzer (clean-room: composition only).
/// </summary>
public sealed class AnalyzerRuleAdapter : IAuditRuleAnalyzer
{
    private readonly IReadOnlyList<string> _ruleKeys;
    private readonly Func<PageAuditContext, CancellationToken, Task<IReadOnlyList<FindingDraft>>> _analyze;

    /// <summary>Creates the adapter over one analyzer delegate.</summary>
    /// <param name="ruleKeys">Rule keys the wrapped analyzer can produce.</param>
    /// <param name="analyze">Analyzer evaluation delegate.</param>
    public AnalyzerRuleAdapter(
        IReadOnlyList<string> ruleKeys,
        Func<PageAuditContext, CancellationToken, Task<IReadOnlyList<FindingDraft>>> analyze)
    {
        ArgumentNullException.ThrowIfNull(ruleKeys);
        ArgumentNullException.ThrowIfNull(analyze);
        _ruleKeys = ruleKeys;
        _analyze = analyze;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedRuleKeys => _ruleKeys;

    /// <inheritdoc />
    public Task<IReadOnlyList<FindingDraft>> AnalyzeAsync(
        PageAuditContext context,
        CancellationToken cancellationToken = default) =>
        _analyze(context, cancellationToken);
}
