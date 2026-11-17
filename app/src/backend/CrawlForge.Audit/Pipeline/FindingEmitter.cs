using System.Globalization;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Audit.Pipeline;

/// <summary>
/// Persists findings with suppression-aware emission (FS-CRAWL-001 §7.5):
/// suppressions are project-scoped, reversible, and carry across re-audits of
/// the same project. Suppressed findings remain persisted and inspectable
/// (IsSuppressed flag) but are excluded from default counts and scoring.
/// Evidence is enriched with the first-seen audit reference for delta tracking
/// and, when registered, the rule's priority and remediation difficulty.
/// </summary>
public sealed class FindingEmitter
{
    private readonly IReadRepository<FindingSuppressionDto> _suppressions;
    private readonly IWriteRepository<FindingDto> _findings;
    private readonly AuditRuleCatalog? _catalog;

    /// <summary>Creates the emitter over the locked repositories.</summary>
    /// <param name="suppressions">Suppression read repository.</param>
    /// <param name="findings">Finding write repository.</param>
    /// <param name="catalog">Optional rule catalog for evidence enrichment.</param>
    public FindingEmitter(
        IReadRepository<FindingSuppressionDto> suppressions,
        IWriteRepository<FindingDto> findings,
        AuditRuleCatalog? catalog = null)
    {
        _suppressions = suppressions;
        _findings = findings;
        _catalog = catalog;
    }

    /// <summary>Emits drafts as persisted findings with suppression flags.</summary>
    /// <param name="auditId">Owning audit.</param>
    /// <param name="projectId">Owning project (suppression scope).</param>
    /// <param name="drafts">Finding drafts from the pipeline.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted findings, in draft order.</returns>
    public async Task<IReadOnlyList<FindingDto>> EmitAsync(
        long auditId,
        long projectId,
        IReadOnlyList<FindingDraft> drafts,
        CancellationToken cancellationToken = default)
    {
        var suppressions = await LoadSuppressionsAsync(projectId, cancellationToken);
        var persisted = new List<FindingDto>(drafts.Count);

        foreach (var draft in drafts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var evidence = EnrichEvidence(draft, auditId);
            var dto = new FindingDto(
                Id: 0,
                AuditId: auditId,
                RuleKey: draft.RuleKey,
                Category: draft.Category,
                Severity: draft.Severity,
                UrlId: draft.UrlId,
                EvidenceJson: evidence,
                RecommendationKey: draft.RecommendationKey,
                IsSuppressed: IsSuppressed(draft, suppressions));
            persisted.Add(await _findings.CreateAsync(dto, cancellationToken));
        }

        return persisted;
    }

    /// <summary>
    /// Determines whether a project suppression covers a finding. A suppression
    /// covers the finding when every declared dimension matches: rule key
    /// (null = any rule), URL scope (null = any URL), and free-form finding
    /// scope (matched against the rule key). A row with no declared dimension
    /// suppresses nothing (guards against accidental blanket suppression).
    /// </summary>
    /// <param name="draft">Finding draft.</param>
    /// <param name="suppressions">Active project suppressions.</param>
    /// <returns>True when an active suppression covers the finding.</returns>
    public static bool IsSuppressed(FindingDraft draft, IReadOnlyList<FindingSuppressionDto> suppressions)
    {
        foreach (var suppression in suppressions)
        {
            if (!suppression.IsSuppressed)
            {
                continue;
            }

            var hasDimension = suppression.RuleKey is not null
                || suppression.UrlId is not null
                || suppression.FindingScope is not null;
            if (!hasDimension)
            {
                continue;
            }

            var ruleMatches = suppression.RuleKey is null ||
                string.Equals(suppression.RuleKey, draft.RuleKey, StringComparison.Ordinal);
            var urlMatches = suppression.UrlId is null || suppression.UrlId == draft.UrlId;
            var scopeMatches = suppression.FindingScope is null ||
                string.Equals(suppression.FindingScope, draft.RuleKey, StringComparison.Ordinal);

            if (ruleMatches && urlMatches && scopeMatches)
            {
                return true;
            }
        }

        return false;
    }

    private async Task<IReadOnlyList<FindingSuppressionDto>> LoadSuppressionsAsync(
        long projectId,
        CancellationToken cancellationToken)
    {
        var page = await _suppressions.ListAsync(
            new ListQuery(
                Limit: 10000,
                Filters: new Dictionary<string, string>
                {
                    ["ProjectId"] = projectId.ToString(CultureInfo.InvariantCulture),
                }),
            cancellationToken);
        return page.Items;
    }

    private string EnrichEvidence(FindingDraft draft, long auditId)
    {
        var fields = new Dictionary<string, object?>
        {
            // First-seen audit reference for delta tracking (§7.5). Cross-audit
            // delta resolution is a reporting concern (phase-05+); emission
            // records the observing audit as the first-seen reference.
            ["firstSeenAuditId"] = auditId,
        };

        if (_catalog is not null && _catalog.TryGet(draft.RuleKey, out var definition) && definition is not null)
        {
            fields["priority"] = definition.Priority;
            if (definition.RemediationDifficulty is not null)
            {
                fields["remediationDifficulty"] = definition.RemediationDifficulty;
            }
        }

        return PipelineDiagnostics.MergeEvidence(draft.EvidenceJson, fields);
    }
}
