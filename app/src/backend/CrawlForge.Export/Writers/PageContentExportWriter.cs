using System.Data.Common;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CrawlForge.Audit.ContentQuality;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Export.Writers;

/// <summary>Full-page content export shapes (FS-CRAWL-001 §8.4 CR-017).</summary>
public enum PageContentFormat
{
    /// <summary>Plain visible text per page (CAP-G-164).</summary>
    PlainText = 0,

    /// <summary>Markdown content per page (CAP-G-165).</summary>
    Markdown = 1,
}

/// <summary>
/// Full-page content export (WP-09-10, CAP-G-164/165; FS-CRAWL-001 §8.4
/// CR-017: "full page text export per page as plain text, alongside the
/// Markdown export"). Streams stored page artifacts from the local content
/// store (§9.6) — one zip entry per page, fully offline. The rendered DOM
/// snapshot is preferred when present (it is the document the audit actually
/// analyzed); otherwise the raw response body is used. Pages without a stored
/// artifact are skipped.
/// <para>
/// Bundle shape: <c>{urlId}-{slug}.txt|.md</c> entries plus a
/// <c>manifest.json</c> mapping every entry to its stable identifiers
/// (urlId, canonicalUrl, artifactKind) — the §11.8 identifier guarantee for
/// per-page files. Plain-text entries carry the pure visible text; Markdown
/// entries lead with the page title and a source line, then the converted
/// content.
/// </para>
/// </summary>
public sealed class PageContentExportWriter
{
    private readonly IDbProvider _provider;
    private readonly IContentStore _contentStore;
    private readonly ITolerantDomParser _parser;

    /// <summary>Creates the writer over the local persistence contracts.</summary>
    /// <param name="provider">Database provider (locked contract).</param>
    /// <param name="contentStore">Content store (locked contract).</param>
    /// <param name="parser">Tolerant DOM parser (locked contract).</param>
    public PageContentExportWriter(IDbProvider provider, IContentStore contentStore, ITolerantDomParser parser)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(contentStore);
        ArgumentNullException.ThrowIfNull(parser);
        _provider = provider;
        _contentStore = contentStore;
        _parser = parser;
    }

    /// <summary>
    /// Writes the per-page content bundle to the output stream (left open).
    /// </summary>
    /// <param name="output">Destination stream (local .zip file).</param>
    /// <param name="auditId">Audit whose stored pages are exported.</param>
    /// <param name="format">Plain text or Markdown.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of pages written.</returns>
    public async Task<long> WriteAsync(Stream output, long auditId, PageContentFormat format, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        var extension = format == PageContentFormat.PlainText ? ".txt" : ".md";
        var manifest = new List<(string Entry, long UrlId, string CanonicalUrl, int ArtifactKind)>();

        await using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            await using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            // RenderedDom (2) sorts before Body (0): the first artifact per URL
            // is the effective analyzed document when rendering was enabled.
            command.CommandText = "SELECT u.`id`, u.`canonical_url`, u.`path`, a.`storage_key`, a.`kind` FROM `url_records` u JOIN `content_artifacts` a ON a.`audit_id` = u.`audit_id` AND a.`url_id` = u.`id` WHERE u.`audit_id` = @auditId AND a.`kind` IN (0, 2) ORDER BY u.`id`, a.`kind` DESC";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@auditId";
            parameter.Value = auditId;
            command.Parameters.Add(parameter);

            long lastUrlId = -1;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var urlId = reader.GetInt64(0);
                if (urlId == lastUrlId)
                {
                    continue; // already exported from the preferred artifact
                }

                lastUrlId = urlId;
                var canonicalUrl = reader.GetString(1);
                var path = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                var storageKey = reader.GetString(3);
                var kind = reader.GetInt32(4);

                var content = await RenderPageAsync(storageKey, canonicalUrl, format, cancellationToken).ConfigureAwait(false);
                if (content is null)
                {
                    continue; // artifact pruned or unreadable — skip, never fail the export
                }

                var entryName = urlId.ToString(CultureInfo.InvariantCulture) + "-" + Slug(path) + extension;
                var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                await using var entryStream = entry.Open();
                await using var writer = new StreamWriter(entryStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                manifest.Add((entryName, urlId, canonicalUrl, kind));
            }

            var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
            await using var manifestStream = manifestEntry.Open();
            await using var manifestWriter = new Utf8JsonWriter(manifestStream);
            manifestWriter.WriteStartObject();
            manifestWriter.WriteNumber("auditId", auditId);
            manifestWriter.WriteString("format", format == PageContentFormat.PlainText ? "text" : "markdown");
            manifestWriter.WriteNumber("pageCount", manifest.Count);
            manifestWriter.WriteStartArray("pages");
            foreach (var (entry, urlId, canonicalUrl, kind) in manifest)
            {
                manifestWriter.WriteStartObject();
                manifestWriter.WriteString("entry", entry);
                manifestWriter.WriteString("urlId", urlId.ToString(CultureInfo.InvariantCulture));
                manifestWriter.WriteString("canonicalUrl", canonicalUrl);
                manifestWriter.WriteString("artifactKind", ((ContentArtifactKind)kind).ToString());
                manifestWriter.WriteEndObject();
            }

            manifestWriter.WriteEndArray();
            manifestWriter.WriteEndObject();
            await manifestWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return manifest.Count;
    }

    private async Task<string?> RenderPageAsync(string storageKey, string canonicalUrl, PageContentFormat format, CancellationToken cancellationToken)
    {
        await using var stream = await _contentStore.OpenReadAsync(storageKey, cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var html = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var document = _parser.Parse(html, canonicalUrl);
        if (format == PageContentFormat.PlainText)
        {
            return VisibleTextExtractor.ExtractVisibleText(document);
        }

        var title = VisibleTextExtractor.ExtractTitle(document);
        var header = "# " + (string.IsNullOrWhiteSpace(title) ? canonicalUrl : title) + "\n\n> Source: " + canonicalUrl + "\n\n";
        return header + HtmlMarkdownConverter.Convert(document);
    }

    private static string Slug(string path)
    {
        var segment = path.Trim('/');
        var last = segment.Length == 0 ? string.Empty : segment.Split('/')[^1];
        var builder = new StringBuilder(last.Length);
        foreach (var ch in last)
        {
            builder.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length == 0)
        {
            slug = "page";
        }

        return slug.Length <= 60 ? slug : slug[..60];
    }
}