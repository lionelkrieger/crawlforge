using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Audit.Scoring;

/// <summary>
/// Project-scoped finding suppression service (FS-CRAWL-001 §7.5): reversible
/// suppressions that carry across re-audits of the same project. Suppressed
/// findings remain persisted and inspectable but are excluded from default
/// counts and scoring. Coverage semantics are EXACTLY the accepted pipeline
/// keystone's (<c>FindingEmitter.IsSuppressed</c>) — see <see cref="SuppressionMatcher"/>.
/// Suppression keys are deterministic per dimension set, so re-suppressing the
/// same target reactivates the existing row instead of duplicating it.
/// Unsuppressing flips the reversible <c>IsSuppressed</c> flag (the row is
/// retained for history; the keystone skips inactive rows). When a recompute
/// coordinator is supplied, every state change triggers offline re-flagging,
/// rescoring, and aggregate refresh for all audits of the project — no URL is
/// ever re-fetched.
/// </summary>
public sealed class FindingSuppressionService : IFindingSuppressionService
{
    private const int SuppressionKeyMaxLength = 191;
    private const int LoadLimit = 10000;

    private readonly IReadRepository<FindingSuppressionDto> _reads;
    private readonly IWriteRepository<FindingSuppressionDto> _writes;
    private readonly ScoringRecomputeCoordinator? _recompute;

    /// <summary>Creates the service over the locked suppression repositories.</summary>
    /// <param name="reads">Suppression read repository.</param>
    /// <param name="writes">Suppression write repository.</param>
    /// <param name="recompute">
    /// Optional offline recompute coordinator; when supplied, suppression state
    /// changes re-flag findings and refresh scores/aggregates for the project.
    /// </param>
    public FindingSuppressionService(
        IReadRepository<FindingSuppressionDto> reads,
        IWriteRepository<FindingSuppressionDto> writes,
        ScoringRecomputeCoordinator? recompute = null)
    {
        _reads = reads ?? throw new ArgumentNullException(nameof(reads));
        _writes = writes ?? throw new ArgumentNullException(nameof(writes));
        _recompute = recompute;
    }

    /// <inheritdoc />
    public async Task<FindingSuppressionDto> SuppressAsync(
        long projectId,
        FindingSuppressionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = BuildSuppressionKey(request);
        var existing = (await LoadProjectSuppressionsAsync(projectId, cancellationToken))
            .FirstOrDefault(s => string.Equals(s.SuppressionKey, key, StringComparison.Ordinal));

        FindingSuppressionDto persisted;
        if (existing is not null)
        {
            if (existing.IsSuppressed)
            {
                // Idempotent: the same active suppression is returned unchanged.
                return existing;
            }

            persisted = await _writes.UpdateAsync(existing with { IsSuppressed = true }, cancellationToken);
        }
        else
        {
            persisted = await _writes.CreateAsync(
                new FindingSuppressionDto(
                    Id: 0,
                    ProjectId: projectId,
                    SuppressionKey: key,
                    RuleKey: request.RuleKey,
                    UrlId: request.UrlId,
                    FindingScope: request.FindingScope,
                    IsSuppressed: true,
                    CreatedAt: DateTimeOffset.UtcNow),
                cancellationToken);
        }

        if (_recompute is not null)
        {
            await _recompute.RecomputeForProjectAsync(projectId, cancellationToken);
        }

        return persisted;
    }

    /// <inheritdoc />
    public async Task<bool> UnsuppressAsync(
        long projectId,
        string suppressionKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(suppressionKey);
        var existing = (await LoadProjectSuppressionsAsync(projectId, cancellationToken))
            .FirstOrDefault(s => string.Equals(s.SuppressionKey, suppressionKey, StringComparison.Ordinal));
        if (existing is null || !existing.IsSuppressed)
        {
            return false;
        }

        await _writes.UpdateAsync(existing with { IsSuppressed = false }, cancellationToken);
        if (_recompute is not null)
        {
            await _recompute.RecomputeForProjectAsync(projectId, cancellationToken);
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FindingSuppressionDto>> ListAsync(
        long projectId,
        CancellationToken cancellationToken = default)
    {
        var rows = await LoadProjectSuppressionsAsync(projectId, cancellationToken);
        return rows.OrderBy(s => s.Id).ToArray();
    }

    /// <inheritdoc />
    public bool IsSuppressed(FindingSuppressionDto suppression, string ruleKey, long? urlId) =>
        SuppressionMatcher.Covers(suppression, ruleKey, urlId);

    /// <summary>
    /// Builds the deterministic suppression key for a dimension set. Keys longer
    /// than the schema's 191-character limit fall back to a SHA-256 digest.
    /// </summary>
    /// <param name="request">Suppression target dimensions.</param>
    /// <returns>The stable suppression key.</returns>
    public static string BuildSuppressionKey(FindingSuppressionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var raw = string.Create(
            CultureInfo.InvariantCulture,
            $"rule={request.RuleKey ?? "*"};url={(request.UrlId is { } url ? url.ToString(CultureInfo.InvariantCulture) : "*")};scope={request.FindingScope ?? "*"}");
        if (raw.Length <= SuppressionKeyMaxLength)
        {
            return raw;
        }

        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    private async Task<IReadOnlyList<FindingSuppressionDto>> LoadProjectSuppressionsAsync(
        long projectId,
        CancellationToken cancellationToken)
    {
        var page = await _reads.ListAsync(
            new ListQuery(
                Limit: LoadLimit,
                Filters: new Dictionary<string, string>
                {
                    ["ProjectId"] = projectId.ToString(CultureInfo.InvariantCulture),
                }),
            cancellationToken);
        return page.Items;
    }
}
