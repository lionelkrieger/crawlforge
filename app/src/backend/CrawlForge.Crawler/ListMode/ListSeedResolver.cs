using System.Data.Common;
using System.Text.Json;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Crawler.ListMode;

/// <summary>
/// Resolved list-mode seeds of one audit (FS-CRAWL-002 §10.2). The URL list is
/// persisted as a JSON array of URL strings in <c>audits.url_list_json</c>
/// (and <c>crawl_configurations.url_list_json</c>); this resolver makes the
/// seeds readable from the persisted payload BEFORE the crawl starts — the
/// component-side half of G10 (the start-before-persist ordering in
/// <c>Api/Controllers/AuditsController.cs</c> is the Api workpiece's half).
/// Malformed entries never fail the audit: they surface as bounded per-entry
/// diagnostics, exactly like the §10.2 ingestion pipeline.
/// </summary>
/// <param name="IsListMode">The persisted list-mode flag of the audit.</param>
/// <param name="Urls">Validated absolute http(s) seed URLs in list order.</param>
/// <param name="Diagnostics">Bounded per-entry rejection diagnostics.</param>
/// <param name="InvalidCount">Total rejected entries (may exceed the diagnostic bound).</param>
public sealed record ListSeedResolution(
    bool IsListMode,
    IReadOnlyList<string> Urls,
    IReadOnlyList<ListLineDiagnostic> Diagnostics,
    int InvalidCount);

/// <summary>
/// Reads list-mode seeds from the persisted <c>UrlListJson</c> payload
/// (FS-CRAWL-002 §10.2/§10.6). Purely local: the JSON shape is the string
/// array written by the audit-start surface; every entry is validated through
/// the same <see cref="ListInputParser"/> rules as file/clipboard ingestion.
/// The engine consults this resolver at crawl start (before seeding the
/// frontier) so list-mode audits crawl the uploaded list, not only
/// <c>SeedsJson</c>.
/// </summary>
public static class ListSeedResolver
{
    /// <summary>
    /// Resolves seeds from a persisted <c>UrlListJson</c> payload without any
    /// database access (the caller already holds the audit/configuration DTO).
    /// </summary>
    /// <param name="urlListJson">Persisted JSON array of URL strings; null/empty means no list.</param>
    /// <param name="isListMode">The persisted list-mode flag; null is treated as false.</param>
    /// <returns>The resolved seeds plus bounded diagnostics.</returns>
    public static ListSeedResolution Resolve(string? urlListJson, bool? isListMode)
    {
        var flag = isListMode == true;
        if (string.IsNullOrWhiteSpace(urlListJson))
        {
            return new ListSeedResolution(flag, [], [], 0);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(urlListJson);
        }
        catch (JsonException ex)
        {
            return new ListSeedResolution(
                flag,
                [],
                [new ListLineDiagnostic(0, Truncate(urlListJson), $"urlListJson is not valid JSON: {ex.Message}")],
                1);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new ListSeedResolution(
                    flag,
                    [],
                    [new ListLineDiagnostic(0, Truncate(urlListJson), "urlListJson must be a JSON array of URL strings.")],
                    1);
            }

            var urls = new List<string>();
            var diagnostics = new List<ListLineDiagnostic>();
            var invalidCount = 0;
            var line = 0;

            foreach (var element in document.RootElement.EnumerateArray())
            {
                line++;
                if (element.ValueKind != JsonValueKind.String)
                {
                    invalidCount++;
                    AddDiagnostic(diagnostics, line, element.GetRawText(), "List entry must be a URL string.");
                    continue;
                }

                var raw = element.GetString() ?? string.Empty;
                if (!ListInputParser.TryValidateUrl(raw.Trim(), out var reason))
                {
                    invalidCount++;
                    AddDiagnostic(diagnostics, line, raw, reason ?? "invalid URL");
                    continue;
                }

                urls.Add(raw.Trim());
            }

            return new ListSeedResolution(flag, urls, diagnostics, invalidCount);
        }
    }

    /// <summary>
    /// Reads the persisted audit row (<c>url_list_json</c>, <c>is_list_mode</c>)
    /// and resolves its list seeds — a purely local database read proving the
    /// seeds are readable before the crawl starts. A missing audit row resolves
    /// to a non-list empty result.
    /// </summary>
    /// <param name="provider">Configured database provider.</param>
    /// <param name="auditId">Audit identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved seeds plus bounded diagnostics.</returns>
    public static async Task<ListSeedResolution> ResolveForAuditAsync(
        IDbProvider provider,
        long auditId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        await using var connection = await provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT `url_list_json`, `is_list_mode` FROM `audits` WHERE `id` = @auditId";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@auditId";
        parameter.Value = auditId;
        command.Parameters.Add(parameter);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new ListSeedResolution(false, [], [], 0);
        }

        var urlListJson = reader.IsDBNull(0) ? null : reader.GetString(0);
        bool? isListMode = reader.IsDBNull(1) ? null : reader.GetInt64(1) != 0;
        return Resolve(urlListJson, isListMode);
    }

    private static void AddDiagnostic(List<ListLineDiagnostic> diagnostics, int line, string raw, string reason)
    {
        if (diagnostics.Count < ListInputParser.MaxDiagnostics)
        {
            diagnostics.Add(new ListLineDiagnostic(line, Truncate(raw), reason));
        }
    }

    private static string Truncate(string raw) =>
        raw.Length <= 200 ? raw : raw[..200] + "…";
}
