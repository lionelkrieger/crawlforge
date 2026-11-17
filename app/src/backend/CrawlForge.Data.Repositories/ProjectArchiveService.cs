using System.Data.Common;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using CrawlForge.Contracts.Entities;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Data.Repositories;

/// <summary>
/// Project archive manifest (entry <c>manifest.json</c>). Archives are self-contained
/// local bundles; no remote service is involved (FS-CRAWL-001 §2.4 / §9).
/// </summary>
/// <param name="FormatVersion">Archive format version (currently 1).</param>
/// <param name="ProjectId">Source project identifier.</param>
/// <param name="ExportedAtUtc">Export timestamp (UTC).</param>
/// <param name="TableRows">Row counts per exported table.</param>
/// <param name="ArtifactFileCount">Number of compressed artifact files included.</param>
public sealed record ProjectArchiveManifest(
    int FormatVersion,
    long ProjectId,
    DateTimeOffset ExportedAtUtc,
    IReadOnlyDictionary<string, long> TableRows,
    int ArtifactFileCount);

/// <summary>
/// Project archive export/import (FS-CRAWL-001 §2.4, §9): a self-contained local
/// ZIP bundle with one JSONL stream per project-scoped table plus the compressed
/// content-store files — full fidelity, enabling offline transfer and explicit
/// user-initiated provider revert. Export streams rows one at a time (bounded
/// memory); import remaps every surrogate key and commits atomically, restoring
/// artifact files inside the same transaction boundary.
/// </summary>
public sealed class ProjectArchiveService : IProjectArchiveService
{
    private const int CurrentFormatVersion = 1;
    private const string ManifestEntryName = "manifest.json";
    private const string DataPrefix = "data/";
    private const string ArtifactPrefix = "artifacts/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private sealed record TablePlan(
        string Table,
        Type DtoType,
        string ExportWhere,
        IReadOnlyList<(string Property, string RefTable)> Remap);

    private const string ProjectScope = "project_id = @project";
    private const string AuditScope = "audit_id IN (SELECT id FROM audits WHERE project_id = @project)";
    private const string UrlScope = "url_id IN (SELECT id FROM url_records WHERE audit_id IN (SELECT id FROM audits WHERE project_id = @project))";

    private static readonly IReadOnlyList<TablePlan> Plans = BuildPlans();

    private readonly IDbProvider _provider;
    private readonly IArtifactFileStore _artifactFiles;

    /// <summary>Creates the archive service.</summary>
    /// <param name="provider">The configured database provider.</param>
    /// <param name="artifactFiles">File-level access to the compressed content store.</param>
    public ProjectArchiveService(IDbProvider provider, IArtifactFileStore artifactFiles)
    {
        _provider = provider;
        _artifactFiles = artifactFiles;
    }

    /// <inheritdoc />
    public async Task ExportAsync(long projectId, Stream destination, CancellationToken cancellationToken = default)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        var tableRows = new Dictionary<string, long>();
        var parameters = new Dictionary<string, object?> { ["project"] = projectId };

        foreach (var plan in Plans)
        {
            var entry = archive.CreateEntry(DataPrefix + plan.Table + ".jsonl", CompressionLevel.Optimal);
            await using var entryStream = entry.Open();
            await using var writer = new StreamWriter(entryStream, leaveOpen: true);
            long rows = 0;
            await foreach (var entity in RepositoryOps.StreamAsync(
                EntityMap.For(plan.DtoType), _provider, plan.ExportWhere, parameters, connection, transaction, cancellationToken))
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(entity, plan.DtoType, JsonOptions));
                rows++;
            }

            await writer.FlushAsync(cancellationToken);
            tableRows[plan.Table] = rows;
        }

        // Content-store files: one entry per distinct content-hash key referenced by
        // the project's audits (deduplicated across audits).
        var artifactKeys = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var artifact in RepositoryOps.StreamAsync(
            EntityMap.For(typeof(ContentArtifactDto)), _provider, AuditScope, parameters, connection, transaction, cancellationToken))
        {
            artifactKeys.Add(((ContentArtifactDto)artifact).StorageKey);
        }

        foreach (var key in artifactKeys)
        {
            await using var source = await _artifactFiles.OpenCompressedAsync(key, cancellationToken)
                ?? throw new InvalidDataException($"Content-store file missing for storage key '{key}'.");
            var entry = archive.CreateEntry(ArtifactPrefix + key, CompressionLevel.NoCompression);
            await using var target = entry.Open();
            await source.CopyToAsync(target, cancellationToken);
        }

        var manifest = new ProjectArchiveManifest(CurrentFormatVersion, projectId, DateTimeOffset.UtcNow, tableRows, artifactKeys.Count);
        var manifestEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Optimal);
        await using var manifestStream = manifestEntry.Open();
        await JsonSerializer.SerializeAsync(manifestStream, manifest, JsonOptions, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ProjectArchiveImportResult> ImportAsync(Stream archive, CancellationToken cancellationToken = default)
    {
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);

        var manifestEntry = zip.GetEntry(ManifestEntryName)
            ?? throw new InvalidDataException("Archive has no manifest.json entry.");
        await using var manifestStream = manifestEntry.Open();
        var manifest = await JsonSerializer.DeserializeAsync<ProjectArchiveManifest>(manifestStream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("Archive manifest is unreadable.");
        if (manifest.FormatVersion != CurrentFormatVersion)
        {
            throw new InvalidDataException($"Unsupported archive format version {manifest.FormatVersion}.");
        }

        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var remap = new Dictionary<string, Dictionary<long, long>>(StringComparer.Ordinal);
        var newlyCreatedFiles = new List<string>();
        long auditsImported = 0;
        long urlsImported = 0;
        long newProjectId = 0;

        try
        {
            foreach (var plan in Plans)
            {
                var entry = zip.GetEntry(DataPrefix + plan.Table + ".jsonl");
                if (entry is null)
                {
                    continue;
                }

                var map = EntityMap.For(plan.DtoType);
                var idMap = new Dictionary<long, long>();
                remap[plan.Table] = idMap;

                await using var entryStream = entry.Open();
                using var reader = new StreamReader(entryStream, leaveOpen: true);
                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    var node = JsonNode.Parse(line)?.AsObject()
                        ?? throw new InvalidDataException($"Malformed row in {plan.Table}.");
                    var oldId = node["id"]?.GetValue<long>()
                        ?? throw new InvalidDataException($"Row without id in {plan.Table}.");

                    foreach (var (property, refTable) in plan.Remap)
                    {
                        // JSONL payloads are camelCase; remap rules name DTO properties.
                        var jsonKey = JsonNamingPolicy.CamelCase.ConvertName(property);
                        if (node[jsonKey] is null)
                        {
                            continue;
                        }

                        var referenced = node[jsonKey]!.GetValue<long>();
                        if (!remap.TryGetValue(refTable, out var refMap) || !refMap.TryGetValue(referenced, out var mapped))
                        {
                            throw new InvalidDataException($"Dangling reference {plan.Table}.{property} → {refTable}#{referenced}.");
                        }

                        node[jsonKey] = mapped;
                    }

                    var entity = node.Deserialize(plan.DtoType, JsonOptions)
                        ?? throw new InvalidDataException($"Unreadable row in {plan.Table}.");
                    var created = await RepositoryOps.CreateAsync(map, _provider.Kind, entity, connection, transaction, cancellationToken);
                    idMap[oldId] = Convert.ToInt64(map.KeyColumn.GetValue(created));
                }

                switch (plan.Table)
                {
                    case "projects":
                        newProjectId = idMap.Values.FirstOrDefault();
                        break;
                    case "audits":
                        auditsImported = idMap.Count;
                        break;
                    case "url_records":
                        urlsImported = idMap.Count;
                        break;
                }
            }

            // Restore compressed artifact files (deduplicated; existing keys are kept).
            foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith(ArtifactPrefix, StringComparison.Ordinal)))
            {
                var key = entry.FullName[ArtifactPrefix.Length..];
                if (_artifactFiles.Exists(key))
                {
                    continue;
                }

                await using var entryStream = entry.Open();
                if (await _artifactFiles.SaveCompressedAsync(key, entryStream, cancellationToken))
                {
                    newlyCreatedFiles.Add(key);
                }
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // Transaction already completed or connection lost; nothing to roll back.
            }

            foreach (var key in newlyCreatedFiles)
            {
                try
                {
                    await _artifactFiles.DeleteCompressedAsync(key, CancellationToken.None);
                }
                catch (IOException)
                {
                    // Best-effort cleanup; orphaned files are reclaimable by retention.
                }
            }

            throw;
        }

        if (newProjectId == 0)
        {
            throw new InvalidDataException("Archive contains no project row.");
        }

        return new ProjectArchiveImportResult(newProjectId, (int)auditsImported, (int)urlsImported);
    }

    private static IReadOnlyList<TablePlan> BuildPlans()
    {
        static TablePlan Plan<TDto>(string table, string where, params (string, string)[] remap) =>
            new(table, typeof(TDto), where, remap);

        // Dependency order: parents before children; the same order drives import.
        return
        [
            Plan<ProjectDto>("projects", "id = @project"),
            Plan<ContentScopeProfileDto>("content_scope_profiles", ProjectScope, ("ProjectId", "projects")),
            Plan<CrawlConfigurationDto>("crawl_configurations", ProjectScope, ("ProjectId", "projects"), ("ContentScopeProfileId", "content_scope_profiles")),
            Plan<AuditDto>("audits", ProjectScope, ("ProjectId", "projects"), ("ConfigurationId", "crawl_configurations")),
            Plan<ScheduleDefinitionDto>("schedule_definitions", ProjectScope, ("ProjectId", "projects"), ("ConfigurationId", "crawl_configurations")),
            Plan<UrlRecordDto>("url_records", AuditScope, ("AuditId", "audits")),
            Plan<UrlElementDto>("url_elements", UrlScope, ("UrlId", "url_records")),
            Plan<LinkEdgeDto>("link_edges", AuditScope, ("AuditId", "audits"), ("SourceUrlId", "url_records"), ("TargetUrlId", "url_records")),
            Plan<FindingDto>("findings", AuditScope, ("AuditId", "audits"), ("UrlId", "url_records")),
            Plan<FindingSuppressionDto>("finding_suppressions", ProjectScope, ("ProjectId", "projects"), ("UrlId", "url_records")),
            Plan<MetricSummaryDto>("metric_summaries", AuditScope, ("AuditId", "audits")),
            Plan<AuditScoreDto>("audit_scores", AuditScope, ("AuditId", "audits")),
            Plan<CrawlCheckpointDto>("crawl_checkpoints", AuditScope, ("AuditId", "audits")),
            Plan<ContentArtifactDto>("content_artifacts", AuditScope, ("AuditId", "audits"), ("UrlId", "url_records")),
            Plan<UrlEmbeddingDto>("url_embeddings", AuditScope, ("AuditId", "audits"), ("UrlId", "url_records")),
            Plan<ExtractedContentDto>("extracted_content", AuditScope, ("AuditId", "audits"), ("UrlId", "url_records")),
            Plan<ContentSearchResultDto>("content_search_results", AuditScope, ("AuditId", "audits"), ("UrlId", "url_records")),
            Plan<SnippetResultDto>("snippet_results", AuditScope, ("AuditId", "audits"), ("UrlId", "url_records")),
            Plan<TechnologyDetectionDto>("technology_detections", AuditScope, ("AuditId", "audits"), ("UrlId", "url_records")),
            Plan<WafDetectionDto>("waf_detections", AuditScope, ("AuditId", "audits"), ("UrlId", "url_records")),
            Plan<HostThrottleEventDto>("host_throttle_events", AuditScope, ("AuditId", "audits")),
            Plan<NgramFrequencyDto>("ngram_frequencies", AuditScope, ("AuditId", "audits"), ("UrlId", "url_records")),
            Plan<NgramSiteSummaryDto>("ngram_site_summaries", AuditScope, ("AuditId", "audits")),
            Plan<RedirectMappingDto>("redirect_mappings", AuditScope, ("AuditId", "audits")),
            Plan<SemanticLinkOpportunityDto>("semantic_link_opportunities", AuditScope, ("AuditId", "audits"), ("SourceUrlId", "url_records"), ("TargetUrlId", "url_records")),
            Plan<SemanticClusterDto>("semantic_clusters", AuditScope, ("AuditId", "audits"), ("UrlId", "url_records")),
            Plan<GovernanceMatrixRowDto>("governance_matrix", AuditScope, ("AuditId", "audits")),
            Plan<AnalyticsDetectionDto>("analytics_detections", AuditScope, ("AuditId", "audits"), ("UrlId", "url_records")),
            Plan<ScheduleRunDto>("schedule_runs", "schedule_definition_id IN (SELECT id FROM schedule_definitions WHERE project_id = @project)", ("ScheduleDefinitionId", "schedule_definitions"), ("AuditId", "audits")),
            Plan<LogImportDto>("log_imports", ProjectScope, ("ProjectId", "projects")),
            Plan<LogUrlAggregateDto>("log_url_aggregates", "log_import_id IN (SELECT id FROM log_imports WHERE project_id = @project)", ("LogImportId", "log_imports"))
        ];
    }
}
