using System.Collections.Concurrent;
using System.Data.Common;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Audit.StructuredData;

/// <summary>
/// Result of the hreflang reciprocity site pass (D14 pattern, mirroring
/// <c>NgramSitePassResult</c>): the missing-return findings plus bounded
/// counters for run diagnostics.
/// </summary>
/// <param name="Findings">Missing-reciprocity findings (source-page UrlId stamped).</param>
/// <param name="PageCount">Pages participating in the reciprocity graph.</param>
/// <param name="AnnotatedPageCount">Pages that declared at least one hreflang annotation.</param>
/// <param name="MissingReturnCount">Missing-return findings emitted.</param>
public sealed record HreflangReciprocitySitePassResult(
    IReadOnlyList<FindingDraft> Findings,
    int PageCount,
    int AnnotatedPageCount,
    int MissingReturnCount);

/// <summary>
/// Pipeline post-pass adapter for site-wide hreflang reciprocity (FS-CRAWL-001
/// §7.2 step 5, §7.13, CAP-B-060; WP-09-04). The locked per-URL rule contract
/// (<see cref="IAuditRuleAnalyzer"/>) cannot express a cross-URL judgment, so
/// this adapter follows the established D14 two-phase pattern (the n-gram rule
/// host): the per-URL pass COLLECTS each page's hreflang set and returns no
/// findings; the explicit <see cref="RunSitePassAsync"/> runs once every page
/// has been parsed and emits the missing-return findings through the engine's
/// finding emitter (registration + invocation are WP-09-11).
/// </summary>
/// <remarks>
/// <para>
/// Collection is audit-scoped and thread-safe (pipeline MaxConcurrency=2):
/// only pages that DECLARE at least one annotation are held in memory during
/// the crawl. The site pass streams <c>url_records</c> through the provider
/// (bounded, ordered by id) and completes the graph with crawled pages that
/// declare nothing but are declared as alternates — exactly the pages whose
/// missing return tag must be reported (the fixture hreflang-no-return trap).
/// Pages that are neither annotated nor declared as a target never participate
/// in reciprocity, so the materialized graph stays bounded by the annotation
/// surface, not the URL-dataset size. Targets outside the crawled set are
/// skipped — no remote fetch ever occurs (exclusions E2/E4).
/// </para>
/// <para>
/// Ordering constraint: <see cref="RunSitePassAsync"/> must run AFTER every
/// per-URL pass of the audit (live and re-audit both converge in the analyze
/// phase). The pass drains the audit's collected sets even on failure;
/// <see cref="DiscardAudit"/> is available for cancelled-run cleanup.
/// Single-page analysis (AuditId 0) never collects: it has no site pass.
/// </para>
/// </remarks>
public sealed class HreflangReciprocityRuleAnalyzer : IAuditRuleAnalyzer
{
    private readonly HreflangReciprocityValidator _validator;
    private readonly IDbProvider _provider;
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<string, PageHreflangSet>> _collected = new();

    /// <summary>Creates the post-pass adapter.</summary>
    /// <param name="validator">The offline reciprocity validator.</param>
    /// <param name="provider">Database provider for the bounded url_records site-pass read.</param>
    public HreflangReciprocityRuleAnalyzer(HreflangReciprocityValidator validator, IDbProvider provider)
    {
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedRuleKeys { get; } =
        [StructuredDataRuleKeys.HreflangMissingReciprocity];

    /// <summary>
    /// Per-URL collection pass: records the page's hreflang set (HTML link
    /// elements + HTTP Link headers, the same collector the per-page i18n
    /// analyzer uses) and returns no findings — reciprocity judgments are
    /// site-level and emitted by <see cref="RunSitePassAsync"/>.
    /// </summary>
    /// <param name="context">Page audit context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An empty finding list (collection only).</returns>
    public Task<IReadOnlyList<FindingDraft>> AnalyzeAsync(PageAuditContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        // Single-page analysis carries AuditId 0 and never runs a site pass.
        if (context.AuditId <= 0 || context.Url.StatusClass != CrawlStatusClass.Success)
        {
            return Task.FromResult<IReadOnlyList<FindingDraft>>([]);
        }

        if (context.Url.ContentType is { } contentType &&
            !contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<IReadOnlyList<FindingDraft>>([]);
        }

        var pageUrl = context.Url.CanonicalUrl;
        var document = context.RenderedDocument ?? context.Document;
        var annotations = new List<HreflangAnnotation>();
        annotations.AddRange(HreflangCollector.FromDocument(document, pageUrl));
        annotations.AddRange(HreflangCollector.FromLinkHeaders(context.Fetch?.Headers, pageUrl));
        if (annotations.Count > 0)
        {
            var perAudit = _collected.GetOrAdd(
                context.AuditId,
                _ => new ConcurrentDictionary<string, PageHreflangSet>(StringComparer.Ordinal));
            var normalized = HreflangCollector.NormalizeUrl(pageUrl, pageUrl);
            perAudit[normalized] = new PageHreflangSet(pageUrl, annotations, context.Url.Id);
        }

        return Task.FromResult<IReadOnlyList<FindingDraft>>([]);
    }

    /// <summary>
    /// Site pass: completes the collected annotation sets with the crawled
    /// pages they reference, validates reciprocity offline, and drains the
    /// audit's collection. Findings carry the source page's url_records id and
    /// both-page evidence (see <see cref="HreflangReciprocityValidator"/>).
    /// </summary>
    /// <param name="auditId">Owning audit reference.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The site-pass result with findings and bounded counters.</returns>
    public async Task<HreflangReciprocitySitePassResult> RunSitePassAsync(long auditId, CancellationToken cancellationToken = default)
    {
        if (auditId <= 0)
        {
            return new HreflangReciprocitySitePassResult([], 0, 0, 0);
        }

        try
        {
            var annotated = _collected.TryGetValue(auditId, out var sets)
                ? sets
                : new ConcurrentDictionary<string, PageHreflangSet>(StringComparer.Ordinal);

            // Declared alternate targets (excluding x-default fallbacks and
            // self-references): crawled pages in this set must participate even
            // when they declare no annotations themselves.
            var targets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (sourceUrl, set) in annotated)
            {
                foreach (var annotation in set.Annotations)
                {
                    if (string.Equals(annotation.Hreflang, HreflangCodes.XDefault, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var target = HreflangCollector.NormalizeUrl(annotation.Href, set.Url);
                    if (!string.Equals(target, sourceUrl, StringComparison.Ordinal))
                    {
                        targets.Add(target);
                    }
                }
            }

            var pages = new List<PageHreflangSet>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            await using (var connection = await _provider.OpenConnectionAsync(cancellationToken))
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT id, canonical_url, status_class, content_type FROM url_records WHERE audit_id = @auditId ORDER BY id";
                AddParameter(command, "@auditId", auditId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var id = reader.GetInt64(0);
                    var url = reader.GetString(1);
                    var statusClass = (CrawlStatusClass)reader.GetInt64(2);
                    var contentType = reader.IsDBNull(3) ? null : reader.GetString(3);
                    if (statusClass != CrawlStatusClass.Success ||
                        (contentType is not null && !contentType.Contains("html", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var normalized = HreflangCollector.NormalizeUrl(url, url);
                    if (!seen.Add(normalized))
                    {
                        continue;
                    }

                    if (annotated.TryGetValue(normalized, out var set))
                    {
                        pages.Add(set with { UrlId = id });
                    }
                    else if (targets.Contains(normalized))
                    {
                        pages.Add(new PageHreflangSet(url, [], id));
                    }
                }
            }

            // Defensive: annotated sets whose URL row is absent from the read
            // (never expected — collection runs from url_records) still
            // participate, in deterministic URL order.
            foreach (var (normalized, set) in annotated.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (seen.Add(normalized))
                {
                    pages.Add(set);
                }
            }

            var findings = _validator.Validate(pages, auditId);
            return new HreflangReciprocitySitePassResult(findings, pages.Count, annotated.Count, findings.Count);
        }
        finally
        {
            _collected.TryRemove(auditId, out _);
        }
    }

    /// <summary>
    /// Discards an audit's collected sets without validating (cancelled-run
    /// cleanup; the site pass already drains on success and failure).
    /// </summary>
    /// <param name="auditId">Owning audit reference.</param>
    public void DiscardAudit(long auditId) => _collected.TryRemove(auditId, out _);

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
