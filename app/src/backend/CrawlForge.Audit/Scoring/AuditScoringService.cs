using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Audit.Scoring;

/// <summary>
/// Audit scoring service (FS-CRAWL-001 §8.1, criterion #32): computes a
/// per-category score and an overall score from finding counts and severities
/// weighted by indexable-URL coverage, excluding suppressed findings. Scores
/// are stored per audit (see <see cref="AuditScoreRecorder"/>) so they are
/// comparable across audits and drive the score-trend surface. Computation is
/// fully offline: it reads persisted findings, URL records, and the project's
/// active suppression set — no URL is ever fetched.
///
/// <para><b>Scoring formula (stable, documented for cross-audit comparability):</b></para>
/// <list type="bullet">
/// <item>Severity deduction weights: Error = 10, Warning = 3, Notice = 1, Pass = 0.</item>
/// <item>Per-category penalty = Σ (weight × non-suppressed finding count).</item>
/// <item>Coverage denominator = max(1, indexable URL count) × 10, where an
/// indexable URL is <c>url_records.is_indexable = true</c>. One error per
/// indexable URL therefore exhausts the category budget.</item>
/// <item>Category score = 100 × (1 − min(1, penalty / denominator)), rounded to
/// two decimals. A category with no findings scores 100.</item>
/// <item>Overall score = arithmetic mean of the enabled category scores
/// (rounded to two decimals); overall counts are the category sums.</item>
/// </list>
///
/// <para><b>ENABLED-CATEGORIES CONVENTION (additive, backward-compatible —
/// IMPORTANT for phase-05 API and phase-06 UI):</b> a disabled category
/// contributes no score row and is excluded from the overall mean. Category
/// enablement is read from the audit's frozen configuration snapshot
/// (<c>audits.configuration_snapshot_json</c>) via the optional JSON property
/// <c>enabledCategories</c> (also accepted: <c>EnabledCategories</c>): an array
/// of <see cref="AuditCategory"/> names (case-insensitive) or numeric values.
/// When the property is ABSENT the audit scores ALL categories (all-enabled
/// default). Phase-05's API and phase-06's UI MUST write this key into the
/// configuration snapshot when category toggles are configured; unknown entry
/// values are ignored for forward compatibility. No locked DTO or schema
/// object is touched by this convention.</para>
///
/// <para>Suppression exclusion consumes BOTH the persisted emission-time flag
/// (set by the keystone <c>FindingEmitter</c>) and the CURRENT active project
/// suppression set with the keystone's exact matching semantics
/// (<see cref="SuppressionMatcher"/>): flagged findings never leak into scores,
/// and new suppressions apply immediately even before persisted flags are
/// re-applied by <see cref="ScoringRecomputeCoordinator"/>.</para>
/// </summary>
public sealed class AuditScoringService : IAuditScoringService
{
    /// <summary>Deduction weight of one Error finding.</summary>
    public const double ErrorWeight = 10.0;

    /// <summary>Deduction weight of one Warning finding.</summary>
    public const double WarningWeight = 3.0;

    /// <summary>Deduction weight of one Notice finding.</summary>
    public const double NoticeWeight = 1.0;

    /// <summary>Deduction weight of one Pass finding (passes never deduct).</summary>
    public const double PassWeight = 0.0;

    /// <summary>Deduction budget per indexable URL (one error exhausts it).</summary>
    public const double PenaltyCapacityPerIndexableUrl = 10.0;

    private readonly IDbProvider _provider;

    /// <summary>Creates the scoring service over the configured provider.</summary>
    /// <param name="provider">Database provider (SQLite or MySQL).</param>
    public AuditScoringService(IDbProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditScoreDraft>> ComputeAsync(
        long auditId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);

        var (projectId, snapshotJson) = await ReadAuditAsync(connection, auditId, cancellationToken);
        var enabled = ParseEnabledCategories(snapshotJson);
        var indexableUrls = await CountIndexableUrlsAsync(connection, auditId, cancellationToken);
        var suppressions = await ReadSuppressionsAsync(connection, projectId, cancellationToken);

        var counts = new Dictionary<AuditCategory, int[]>();
        foreach (var category in enabled)
        {
            counts[category] = new int[4];
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT category, severity, rule_key, url_id, is_suppressed FROM findings WHERE audit_id = @auditId";
            AddParameter(command, "@auditId", auditId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var category = (AuditCategory)reader.GetInt32(0);
                if (!counts.TryGetValue(category, out var severityCounts))
                {
                    // Disabled category: contributes no score (§8.1).
                    continue;
                }

                // Consume the emission-time flag (keystone FindingEmitter) AND
                // the current suppression set: flagged findings never leak into
                // scores, and new suppressions apply immediately even before
                // the coordinator re-applies flags.
                var flagged = Convert.ToBoolean(reader.GetValue(4), CultureInfo.InvariantCulture);
                var ruleKey = reader.GetString(2);
                var urlId = reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3);
                if (flagged || SuppressionMatcher.IsSuppressed(ruleKey, urlId, suppressions))
                {
                    continue;
                }

                severityCounts[reader.GetInt32(1)]++;
            }
        }

        var drafts = new List<AuditScoreDraft>(counts.Count + 1);
        var capacity = Math.Max(1, indexableUrls) * PenaltyCapacityPerIndexableUrl;
        double scoreSum = 0;
        int errorSum = 0, warningSum = 0, noticeSum = 0, passSum = 0;
        foreach (var category in enabled.OrderBy(c => c))
        {
            var c = counts[category];
            var penalty = (c[0] * ErrorWeight) + (c[1] * WarningWeight) + (c[2] * NoticeWeight) + (c[3] * PassWeight);
            var score = Math.Round(100.0 * (1.0 - Math.Min(1.0, penalty / capacity)), 2);
            drafts.Add(new AuditScoreDraft(category, score, c[0], c[1], c[2], c[3]));
            scoreSum += score;
            errorSum += c[0];
            warningSum += c[1];
            noticeSum += c[2];
            passSum += c[3];
        }

        var overall = drafts.Count > 0 ? Math.Round(scoreSum / drafts.Count, 2) : 100.0;
        drafts.Add(new AuditScoreDraft(null, overall, errorSum, warningSum, noticeSum, passSum));
        return drafts;
    }

    /// <summary>
    /// Parses the optional <c>enabledCategories</c> snapshot convention. Absent
    /// property = all categories enabled; entries may be enum names
    /// (case-insensitive) or numeric values; unknown entries are ignored.
    /// </summary>
    /// <param name="snapshotJson">Frozen configuration snapshot JSON.</param>
    /// <returns>The enabled category set in enum order.</returns>
    public static IReadOnlyList<AuditCategory> ParseEnabledCategories(string? snapshotJson)
    {
        var all = Enum.GetValues<AuditCategory>();
        if (string.IsNullOrWhiteSpace(snapshotJson))
        {
            return all;
        }

        try
        {
            using var document = JsonDocument.Parse(snapshotJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return all;
            }

            JsonElement array = default;
            var found = false;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "enabledCategories", StringComparison.OrdinalIgnoreCase))
                {
                    array = property.Value;
                    found = true;
                    break;
                }
            }

            if (!found || array.ValueKind != JsonValueKind.Array)
            {
                return all;
            }

            var enabled = new HashSet<AuditCategory>();
            foreach (var entry in array.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Number && entry.TryGetInt32(out var numeric) &&
                    Enum.IsDefined(typeof(AuditCategory), numeric))
                {
                    enabled.Add((AuditCategory)numeric);
                }
                else if (entry.ValueKind == JsonValueKind.String &&
                    Enum.TryParse<AuditCategory>(entry.GetString(), ignoreCase: true, out var named) &&
                    Enum.IsDefined(named))
                {
                    enabled.Add(named);
                }
            }

            return all.Where(enabled.Contains).ToArray();
        }
        catch (JsonException)
        {
            // Malformed snapshots fall back to the all-enabled default.
            return all;
        }
    }

    private static async Task<(long ProjectId, string? SnapshotJson)> ReadAuditAsync(
        DbConnection connection,
        long auditId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT project_id, configuration_snapshot_json FROM audits WHERE id = @auditId AND is_deleted = 0";
        AddParameter(command, "@auditId", auditId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException($"Audit {auditId} does not exist or is deleted.");
        }

        return (reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private static async Task<long> CountIndexableUrlsAsync(
        DbConnection connection,
        long auditId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM url_records WHERE audit_id = @auditId AND is_indexable = 1";
        AddParameter(command, "@auditId", auditId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<FindingSuppressionDto>> ReadSuppressionsAsync(
        DbConnection connection,
        long projectId,
        CancellationToken cancellationToken)
    {
        var suppressions = new List<FindingSuppressionDto>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, suppression_key, rule_key, url_id, finding_scope, is_suppressed, created_at " +
            "FROM finding_suppressions WHERE project_id = @projectId";
        AddParameter(command, "@projectId", projectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            suppressions.Add(new FindingSuppressionDto(
                Id: reader.GetInt64(0),
                ProjectId: projectId,
                SuppressionKey: reader.GetString(1),
                RuleKey: reader.IsDBNull(2) ? null : reader.GetString(2),
                UrlId: reader.IsDBNull(3) ? null : reader.GetInt64(3),
                FindingScope: reader.IsDBNull(4) ? null : reader.GetString(4),
                IsSuppressed: Convert.ToBoolean(reader.GetValue(5), CultureInfo.InvariantCulture),
                CreatedAt: DateTimeOffset.Parse(
                    reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
        }

        return suppressions;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
