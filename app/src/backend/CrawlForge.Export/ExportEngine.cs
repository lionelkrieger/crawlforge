using System.Collections.Concurrent;
using System.Globalization;
using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Export.Datasets;
using CrawlForge.Export.Writers;

namespace CrawlForge.Export;

/// <summary>
/// Export engine (locked <see cref="IExportEngine"/>, FS-CRAWL-001 §8.4/§8.7):
/// multi-sheet Excel, CSV-per-dataset zip, self-contained HTML bundle, PDF
/// summary, and XML sitemap — generated from LOCAL aggregates and stored
/// artifacts into valid local files, fully offline (criterion #7).
/// <para>
/// <see cref="CreateExportAsync"/> returns immediately with a queued job;
/// generation runs in the background and progress flows over the
/// <c>export-progress</c> hub event. The scheduler's completion-action hook
/// polls <see cref="GetExportAsync"/> and then delivers through
/// <see cref="IExportDestinationDelivery"/>.
/// </para>
/// <para>
/// <b>Restart semantics:</b> the job registry is in-memory — in-flight jobs
/// are lost on process restart; completed files remain on disk. Durable job
/// history is a deferred schema-amendment candidate (orchestrator ruling).
/// </para>
/// </summary>
public sealed class ExportEngine : IExportEngine, IDisposable
{
    private readonly IDbProvider _provider;
    private readonly ExportJobRegistry _jobs;
    private readonly ExportEventPublisher _events;
    private readonly ExportOptions _options;
    private readonly string? _templateDirectory;
    private readonly IContentStore? _contentStore;
    private readonly ITolerantDomParser? _domParser;
    private readonly ConcurrentDictionary<long, Task> _running = new();
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>Creates the engine.</summary>
    /// <param name="provider">Database provider (locked contract).</param>
    /// <param name="jobs">Job registry (shared with delivery).</param>
    /// <param name="events">Hub event publisher.</param>
    /// <param name="options">Export options.</param>
    /// <param name="templateDirectory">Optional HTML template asset directory.</param>
    /// <param name="contentStore">Content store for full-page text/Markdown exports (WP-09-10; Host wiring).</param>
    /// <param name="domParser">Tolerant DOM parser for full-page content conversion (WP-09-10; Host wiring).</param>
    public ExportEngine(
        IDbProvider provider,
        ExportJobRegistry jobs,
        ExportEventPublisher events,
        ExportOptions? options = null,
        string? templateDirectory = null,
        IContentStore? contentStore = null,
        ITolerantDomParser? domParser = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(events);
        _provider = provider;
        _jobs = jobs;
        _events = events;
        _options = options ?? ExportOptions.Default;
        _templateDirectory = templateDirectory;
        _contentStore = contentStore;
        _domParser = domParser;
    }

    /// <inheritdoc/>
    public async Task<ExportJobDto> CreateExportAsync(ExportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var record = _jobs.Create(request.AuditId, request.Format, request.Datasets);
        await PublishProgressAsync(record, null).ConfigureAwait(false);

        var token = _lifetime.Token;
        var task = Task.Run(() => GenerateAsync(record, token), CancellationToken.None);
        _running[record.Id] = task;
        return record.ToDto();
    }

    /// <inheritdoc/>
    public Task<ExportJobDto?> GetExportAsync(long exportJobId, CancellationToken cancellationToken = default)
    {
        var record = _jobs.Get(exportJobId);
        // Additive-format jobs cannot be represented by the locked enum DTO;
        // they are read through GetAdditiveExportAsync (WP-09-10).
        return Task.FromResult(record is null || record.IsAdditive ? null : record.ToDto());
    }

    /// <summary>
    /// Creates an additive-format export job (WP-09-10: json, jsonl, text,
    /// markdown) and returns immediately. Progress flows over the same
    /// <c>export-progress</c> hub event as baseline jobs. Text/Markdown are
    /// full-page content exports (one file per stored page); dataset selection
    /// applies to json/jsonl only.
    /// </summary>
    /// <param name="auditId">Audit to export.</param>
    /// <param name="formatWire">Additive wire format key.</param>
    /// <param name="datasets">Selected dataset keys (json/jsonl; ignored for page content).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created additive export job.</returns>
    /// <exception cref="ArgumentException">Unknown additive format key.</exception>
    public async Task<AdditiveExportJobDto> CreateAdditiveExportAsync(
        long auditId,
        string formatWire,
        IReadOnlyList<string> datasets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(datasets);
        var record = _jobs.CreateAdditive(auditId, formatWire, datasets);
        await PublishProgressAsync(record, null).ConfigureAwait(false);

        var token = _lifetime.Token;
        var task = Task.Run(() => GenerateAsync(record, token), CancellationToken.None);
        _running[record.Id] = task;
        return record.ToAdditiveDto();
    }

    /// <summary>
    /// Reads any export job through the additive wire projection (baseline
    /// jobs included — the format is the authoritative wire key).
    /// </summary>
    /// <param name="exportJobId">Export job identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The additive job projection, or null when absent.</returns>
    public Task<AdditiveExportJobDto?> GetAdditiveExportAsync(long exportJobId, CancellationToken cancellationToken = default)
        => Task.FromResult(_jobs.Get(exportJobId)?.ToAdditiveDto());

    /// <inheritdoc/>
    public Task<Stream> OpenExportFileAsync(long exportJobId, CancellationToken cancellationToken = default)
    {
        var record = _jobs.Get(exportJobId)
            ?? throw new InvalidOperationException($"Export job {exportJobId} not found.");
        if (record.Status != ExportJobStatus.Completed || record.FilePath is null || !File.Exists(record.FilePath))
        {
            throw new InvalidOperationException($"Export job {exportJobId} has no completed file.");
        }

        return Task.FromResult<Stream>(File.OpenRead(record.FilePath));
    }

    /// <summary>
    /// Awaits a job's background generation (test/diagnostic surface; the
    /// scheduler polls <see cref="GetExportAsync"/> instead).
    /// </summary>
    /// <param name="exportJobId">Job identifier.</param>
    /// <returns>The generation task, or a completed task when unknown.</returns>
    public Task GetJobTask(long exportJobId)
        => _running.TryGetValue(exportJobId, out var task) ? task : Task.CompletedTask;

    /// <inheritdoc/>
    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private async Task GenerateAsync(ExportJobRecord record, CancellationToken cancellationToken)
    {
        try
        {
            _jobs.Set(record with { Status = ExportJobStatus.Running });
            await PublishProgressAsync(_jobs.Get(record.Id)!, null).ConfigureAwait(false);

            Directory.CreateDirectory(_options.OutputDirectory);
            var reader = new ExportDatasetReader(_provider);
            var path = record.IsAdditive
                ? await GenerateAdditiveAsync(record, reader, cancellationToken).ConfigureAwait(false)
                : await GenerateBaselineAsync(record, reader, cancellationToken).ConfigureAwait(false);

            _jobs.Set(record with { Status = ExportJobStatus.Completed, FilePath = path });
            await PublishProgressAsync(_jobs.Get(record.Id)!, null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Generation failure is a failed job + hub event — never an audit failure.
            _jobs.Set(record with { Status = ExportJobStatus.Failed, FailureMessage = ex.Message });
            await PublishProgressAsync(_jobs.Get(record.Id)!, ex.Message).ConfigureAwait(false);
        }
    }

    private async Task<string> GenerateBaselineAsync(ExportJobRecord record, ExportDatasetReader reader, CancellationToken cancellationToken)
    {
        string path;
        switch (record.Format)
        {
            case ExportFormat.Excel:
                path = Path.Combine(_options.OutputDirectory, $"export-{record.Id}.xlsx");
                await using (var file = File.Create(path))
                {
                    await ExcelExportWriter.WriteAsync(file, reader.ReadAsync(record.AuditId, record.Datasets, cancellationToken), cancellationToken).ConfigureAwait(false);
                }

                break;

            case ExportFormat.Csv:
                path = Path.Combine(_options.OutputDirectory, $"export-{record.Id}.zip");
                await using (var file = File.Create(path))
                {
                    await CsvExportWriter.WriteAsync(file, reader.ReadAsync(record.AuditId, record.Datasets, cancellationToken), cancellationToken).ConfigureAwait(false);
                }

                break;

            case ExportFormat.Html:
                path = Path.Combine(_options.OutputDirectory, $"export-{record.Id}.html");
                await using (var file = File.Create(path))
                {
                    await HtmlReportWriter.WriteAsync(file, record.AuditId, reader.ReadAsync(record.AuditId, record.Datasets, cancellationToken), _templateDirectory, cancellationToken).ConfigureAwait(false);
                }

                break;

            case ExportFormat.Pdf:
                path = Path.Combine(_options.OutputDirectory, $"export-{record.Id}.pdf");
                await using (var file = File.Create(path))
                {
                    await PdfSummaryWriter.WriteAsync(file, record.AuditId, reader.ReadAsync(record.AuditId, record.Datasets, cancellationToken), cancellationToken).ConfigureAwait(false);
                }

                break;

            case ExportFormat.XmlSitemap:
                var sitemap = new SitemapWriter(_provider, _options);
                var eligible = await sitemap.CountEligibleUrlsAsync(record.AuditId, cancellationToken).ConfigureAwait(false);
                if (eligible <= _options.SitemapMaxUrlsPerFile)
                {
                    path = Path.Combine(_options.OutputDirectory, $"export-{record.Id}.xml");
                    await using var file = File.Create(path);
                    await sitemap.WriteSingleAsync(file, record.AuditId, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    path = Path.Combine(_options.OutputDirectory, $"export-{record.Id}.zip");
                    var baseUrl = await ResolveBaseUrlAsync(record.AuditId, cancellationToken).ConfigureAwait(false);
                    await using var file = File.Create(path);
                    await sitemap.WriteIndexAsync(file, record.AuditId, baseUrl, cancellationToken).ConfigureAwait(false);
                }

                break;

            default:
                throw new InvalidOperationException($"Unknown export format '{record.Format}'.");
        }

        return path;
    }

    private async Task<string> GenerateAdditiveAsync(ExportJobRecord record, ExportDatasetReader reader, CancellationToken cancellationToken)
    {
        var formatWire = record.FormatWire!;
        var path = Path.Combine(_options.OutputDirectory, $"export-{record.Id}{AdditiveExportFormats.ExtensionFor(formatWire)}");
        await using var file = File.Create(path);
        switch (formatWire)
        {
            case AdditiveExportFormats.Json:
                // Agent contract (§11.8): summary-first compact JSON; every row
                // carries the same stable identifiers as the API.
                await JsonExportWriter.WriteAsync(file, record.AuditId, reader.ReadAsync(record.AuditId, record.Datasets, includeIdentifiers: true, cancellationToken), cancellationToken).ConfigureAwait(false);
                break;

            case AdditiveExportFormats.JsonLines:
                await JsonExportWriter.WriteLinesAsync(file, reader.ReadAsync(record.AuditId, record.Datasets, includeIdentifiers: true, cancellationToken), cancellationToken).ConfigureAwait(false);
                break;

            case AdditiveExportFormats.Text:
            case AdditiveExportFormats.Markdown:
                if (_contentStore is null || _domParser is null)
                {
                    throw new InvalidOperationException(
                        "Full-page content export requires the content store and DOM parser (Host wiring handoff, WP-09-10).");
                }

                var pageWriter = new PageContentExportWriter(_provider, _contentStore, _domParser);
                var pageFormat = formatWire == AdditiveExportFormats.Text ? PageContentFormat.PlainText : PageContentFormat.Markdown;
                await pageWriter.WriteAsync(file, record.AuditId, pageFormat, cancellationToken).ConfigureAwait(false);
                break;

            default:
                throw new InvalidOperationException($"Unknown additive export format '{formatWire}'.");
        }

        return path;
    }

    private async Task<string> ResolveBaseUrlAsync(long auditId, CancellationToken cancellationToken)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT `canonical_url` FROM `url_records` WHERE `audit_id` = @auditId ORDER BY `id` LIMIT 1";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@auditId";
        parameter.Value = auditId;
        command.Parameters.Add(parameter);
        var first = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (first is not null && Uri.TryCreate(first, UriKind.Absolute, out var uri))
        {
            return $"{uri.Scheme}://{uri.Host}";
        }

        return "https://localhost";
    }

    private Task PublishProgressAsync(ExportJobRecord record, string? message)
        => _events.PublishProgressAsync(
            new ExportProgressPayload(
                record.Id.ToString(CultureInfo.InvariantCulture),
                record.AuditId.ToString(CultureInfo.InvariantCulture),
                record.EffectiveFormatWire,
                record.Status.ToWireValue(),
                record.FilePath,
                message));
}
