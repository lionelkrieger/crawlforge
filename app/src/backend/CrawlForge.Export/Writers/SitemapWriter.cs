using System.Data.Common;
using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Export.Writers;

/// <summary>
/// XML sitemap generator (FS-CRAWL-001 §8.7): builds sitemaps from the filtered
/// URL set of an audit — indexable, successfully crawled URLs only — with
/// changefreq/priority hints. Above the per-file URL bound the output is a
/// sitemap index plus split sitemap files inside a zip archive; at or below
/// the bound it is a single sitemap XML document. Fully local and offline.
/// </summary>
public sealed class SitemapWriter
{
    private readonly IDbProvider _provider;
    private readonly ExportOptions _options;

    /// <summary>Creates the generator.</summary>
    /// <param name="provider">Database provider (locked contract).</param>
    /// <param name="options">Export options (sitemap bounds and hints).</param>
    public SitemapWriter(IDbProvider provider, ExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
        _options = options ?? ExportOptions.Default;
    }

    /// <summary>
    /// Counts the URLs eligible for the sitemap (indexable + success status).
    /// The engine uses this to choose the single-file vs index output shape.
    /// </summary>
    /// <param name="auditId">Audit to generate from.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The eligible URL count.</returns>
    public async Task<long> CountEligibleUrlsAsync(long auditId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM `url_records` WHERE `audit_id` = @auditId AND `is_indexable` = 1 AND `status_class` = @success";
        AddParameter(command, "@auditId", auditId);
        AddParameter(command, "@success", (int)CrawlStatusClass.Success);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Writes a single sitemap XML document (caller guarantees the eligible
    /// count fits <see cref="ExportOptions.SitemapMaxUrlsPerFile"/>).
    /// </summary>
    /// <param name="output">Destination stream.</param>
    /// <param name="auditId">Audit to generate from.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of URLs written.</returns>
    public async Task<long> WriteSingleAsync(Stream output, long auditId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        await using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
        await writer.WriteAsync("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n".AsMemory(), cancellationToken).ConfigureAwait(false);
        var count = await WriteUrlsAsync(writer, auditId, skip: 0, take: long.MaxValue, cancellationToken).ConfigureAwait(false);
        await writer.WriteAsync("</urlset>\n".AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        return count;
    }

    /// <summary>
    /// Writes a sitemap index plus split sitemap files into a zip archive.
    /// </summary>
    /// <param name="output">Destination stream.</param>
    /// <param name="auditId">Audit to generate from.</param>
    /// <param name="baseUrl">Base URL the sitemap files will be served from (index loc values).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The total number of URLs written across all files.</returns>
    public async Task<long> WriteIndexAsync(Stream output, long auditId, string baseUrl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        var total = await CountEligibleUrlsAsync(auditId, cancellationToken).ConfigureAwait(false);
        var fileCount = (int)Math.Max(1, (total + _options.SitemapMaxUrlsPerFile - 1) / _options.SitemapMaxUrlsPerFile);
        await using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        var indexEntry = archive.CreateEntry("sitemap-index.xml", CompressionLevel.Optimal);
        await using (var indexStream = indexEntry.Open())
        await using (var indexWriter = new StreamWriter(indexStream, new UTF8Encoding(false)))
        {
            await indexWriter.WriteAsync("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<sitemapindex xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n".AsMemory(), cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < fileCount; i++)
            {
                var name = $"sitemap-{i + 1}.xml";
                var loc = baseUrl.TrimEnd('/') + "/" + name;
                await indexWriter.WriteAsync($"<sitemap><loc>{SecurityElement.Escape(loc)}</loc></sitemap>\n".AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            await indexWriter.WriteAsync("</sitemapindex>\n".AsMemory(), cancellationToken).ConfigureAwait(false);
            await indexWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        long written = 0;
        for (var i = 0; i < fileCount; i++)
        {
            var entry = archive.CreateEntry($"sitemap-{i + 1}.xml", CompressionLevel.Optimal);
            await using var entryStream = entry.Open();
            await using var writer = new StreamWriter(entryStream, new UTF8Encoding(false));
            await writer.WriteAsync("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n".AsMemory(), cancellationToken).ConfigureAwait(false);
            written += await WriteUrlsAsync(writer, auditId, (long)i * _options.SitemapMaxUrlsPerFile, _options.SitemapMaxUrlsPerFile, cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync("</urlset>\n".AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return written;
    }

    private async Task<long> WriteUrlsAsync(TextWriter writer, long auditId, long skip, long take, CancellationToken cancellationToken)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT `canonical_url`, `path` FROM `url_records` WHERE `audit_id` = @auditId AND `is_indexable` = 1 AND `status_class` = @success ORDER BY `canonical_url` LIMIT @take OFFSET @skip";
        AddParameter(command, "@auditId", auditId);
        AddParameter(command, "@success", (int)CrawlStatusClass.Success);
        AddParameter(command, "@take", take);
        AddParameter(command, "@skip", skip);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        long count = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var url = reader.GetString(0);
            var path = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var priority = path is "/" or "" ? _options.SitemapHomePriority : _options.SitemapDefaultPriority;
            await writer.WriteAsync(
                $"<url><loc>{SecurityElement.Escape(url)}</loc><changefreq>{SecurityElement.Escape(_options.SitemapChangeFrequency)}</changefreq><priority>{priority.ToString("0.0", CultureInfo.InvariantCulture)}</priority></url>\n".AsMemory(),
                cancellationToken).ConfigureAwait(false);
            count++;
        }

        return count;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
