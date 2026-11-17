using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Audit.Pipeline;

/// <summary>
/// Local rule-host configuration (FS-CRAWL-001 §7.1: enabled categories and
/// per-rule thresholds stored locally). Null fields impose no restriction.
/// </summary>
public sealed record RuleHostOptions
{
    /// <summary>Enabled taxonomy categories; null enables all.</summary>
    public IReadOnlySet<AuditCategory>? EnabledCategories { get; init; }

    /// <summary>Rule-key filter; null runs every supported rule.</summary>
    public IReadOnlyList<string>? RuleKeyFilter { get; init; }
}

/// <summary>
/// Generic rule host (FS-CRAWL-001 §7.4/§7.6): composes the injected analyzer
/// family at composition time and evaluates each independently. Analyzer
/// failures are isolated as rule-level diagnostics and never fail the audit
/// (FS-CRAWL-002 §4.2 bound philosophy). Analyzers that do not exist yet are
/// simply absent from the injected set — the host tolerates any subset.
/// </summary>
public sealed class AuditRuleHost : IAuditRuleAnalyzer
{
    private readonly IReadOnlyList<IAuditRuleAnalyzer> _analyzers;
    private readonly RuleHostOptions _options;

    /// <summary>Creates the host over the injected analyzer family.</summary>
    /// <param name="analyzers">Available analyzers (any subset, possibly empty).</param>
    /// <param name="options">Optional local rule configuration.</param>
    public AuditRuleHost(IEnumerable<IAuditRuleAnalyzer> analyzers, RuleHostOptions? options = null)
    {
        _analyzers = analyzers.ToArray();
        _options = options ?? new RuleHostOptions();
    }

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedRuleKeys =>
        _analyzers
            .SelectMany(analyzer => analyzer.SupportedRuleKeys)
            .Distinct(StringComparer.Ordinal)
            .Where(key => _options.RuleKeyFilter is null ||
                          _options.RuleKeyFilter.Contains(key, StringComparer.Ordinal))
            .ToArray();

    /// <inheritdoc />
    public async Task<IReadOnlyList<FindingDraft>> AnalyzeAsync(
        PageAuditContext context,
        CancellationToken cancellationToken = default)
    {
        var findings = new List<FindingDraft>();
        foreach (var analyzer in _analyzers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<FindingDraft> produced;
            try
            {
                produced = await analyzer.AnalyzeAsync(context, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Per-rule failure isolation: diagnose and continue (§7.6).
                findings.Add(PipelineDiagnostics.AnalyzerFailure(
                    analyzer.GetType().Name, ex.Message, context.Url.Id));
                continue;
            }

            foreach (var draft in produced)
            {
                if (_options.EnabledCategories is { } categories && !categories.Contains(draft.Category))
                {
                    continue;
                }

                if (_options.RuleKeyFilter is { } filter && !filter.Contains(draft.RuleKey, StringComparer.Ordinal))
                {
                    continue;
                }

                findings.Add(draft);
            }
        }

        return findings;
    }
}
