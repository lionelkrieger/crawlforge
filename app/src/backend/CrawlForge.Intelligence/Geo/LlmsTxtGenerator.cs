using System.Data.Common;
using System.Globalization;
using System.Text;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Intelligence;
using CrawlForge.Contracts.Persistence;

namespace CrawlForge.Intelligence.Geo;

/// <summary>
/// llms.txt / llms-full.txt generator (FS-CRAWL-002 §13.3). Ranking contract:
/// indexable pages with no Error findings, ordered by descending
/// content-quality composite — 0.6 × bounded word count (cap 2000) + 0.2 ×
/// title present + 0.2 × meta description present — grouped by first path
/// segment (directory/category). Output structure: H1 site name; blockquote
/// summary (most frequent meta description, or a local fallback); H2 sections
/// per group with markdown link lists (page title + absolute URL). The
/// optional llms-full.txt variant adds bounded content excerpts (300 chars).
///
/// <para><b>LOCAL ONLY (exclusion E9):</b> generation writes files into the
/// requested local directory. Nothing is ever auto-published or submitted to
/// any vendor; no network access occurs.</para>
/// </summary>
public sealed class LlmsTxtGenerator : ILlmsTxtGenerator
{
    /// <summary>Bound on ranked candidate pages considered.</summary>
    public const int MaxCandidates = 200;

    /// <summary>Bound on pages included in the generated file.</summary>
    public const int MaxIncludedPages = 100;

    /// <summary>Word-count cap for the quality composite.</summary>
    public const int WordCountCap = 2000;

    /// <summary>Excerpt bound for the llms-full.txt variant (characters).</summary>
    public const int ExcerptLength = 300;

    private const int MaxStoredBodyBytes = 2 * 1024 * 1024;

    private readonly IDbProvider _provider;
    private readonly IContentStore _contentStore;
    private readonly ITolerantDomParser _parser;

    /// <summary>Creates the generator over stored data only.</summary>
    /// <param name="provider">Database provider.</param>
    /// <param name="contentStore">Compressed content store (stored bodies).</param>
    /// <param name="parser">Tolerant DOM parser.</param>
    public LlmsTxtGenerator(IDbProvider provider, IContentStore contentStore, ITolerantDomParser parser)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _contentStore = contentStore ?? throw new ArgumentNullException(nameof(contentStore));
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
    }

    /// <inheritdoc />
    public async Task<LlmsGenerationResult> GenerateAsync(
        LlmsGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputDirectory);

        var candidates = await LoadCandidatesAsync(request.AuditId, cancellationToken);
        var pages = new List<RankedPage>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = await ReadStoredBodyAsync(request.AuditId, candidate.Id, cancellationToken);
            if (body is null)
            {
                continue;
            }

            var document = _parser.Parse(body, candidate.CanonicalUrl);
            var title = FindTitle(document.Root) ?? candidate.CanonicalUrl;
            var description = FindMetaDescription(document.Root);
            var text = CollectVisibleText(document.Root);
            var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            var composite =
                (0.6 * Math.Min(words, WordCountCap) / WordCountCap) +
                (title.Length > 0 ? 0.2 : 0) +
                (string.IsNullOrWhiteSpace(description) ? 0 : 0.2);
            pages.Add(new RankedPage(
                candidate.CanonicalUrl, candidate.Path, title, description, text, words, composite));
        }

        var included = pages
            .OrderByDescending(p => p.Composite)
            .ThenBy(p => p.Url, StringComparer.Ordinal)
            .Take(MaxIncludedPages)
            .ToArray();

        var siteName = included.Length > 0 && Uri.TryCreate(included[0].Url, UriKind.Absolute, out var siteUri)
            ? siteUri.Host
            : $"audit-{request.AuditId}";
        var summary = included
            .Where(p => !string.IsNullOrWhiteSpace(p.Description))
            .GroupBy(p => p.Description!, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault() ?? $"Curated documentation and key pages for {siteName}.";

        var groups = included
            .GroupBy(p => GroupKey(p.Path), StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        var standard = new StringBuilder();
        standard.Append("# ").AppendLine(siteName);
        standard.AppendLine();
        standard.Append("> ").AppendLine(summary);
        foreach (var group in groups)
        {
            standard.AppendLine();
            standard.Append("## ").AppendLine(group.Key);
            standard.AppendLine();
            foreach (var page in group.OrderByDescending(p => p.Composite).ThenBy(p => p.Url, StringComparer.Ordinal))
            {
                standard.Append("- [").Append(page.Title).Append("](").Append(page.Url).AppendLine(")");
            }
        }

        Directory.CreateDirectory(request.OutputDirectory);
        var written = new List<string>();
        var standardPath = Path.Combine(request.OutputDirectory, "llms.txt");
        await File.WriteAllTextAsync(standardPath, standard.ToString(), cancellationToken);
        written.Add(standardPath);

        if (request.IncludeFullVersion)
        {
            var full = new StringBuilder();
            full.Append("# ").AppendLine(siteName);
            full.AppendLine();
            full.Append("> ").AppendLine(summary);
            foreach (var group in groups)
            {
                full.AppendLine();
                full.Append("## ").AppendLine(group.Key);
                full.AppendLine();
                foreach (var page in group.OrderByDescending(p => p.Composite).ThenBy(p => p.Url, StringComparer.Ordinal))
                {
                    full.Append("- [").Append(page.Title).Append("](").Append(page.Url).AppendLine(")");
                    var excerpt = Excerpt(page.Text);
                    if (excerpt.Length > 0)
                    {
                        full.Append("  ").AppendLine(excerpt);
                    }
                }
            }

            var fullPath = Path.Combine(request.OutputDirectory, "llms-full.txt");
            await File.WriteAllTextAsync(fullPath, full.ToString(), cancellationToken);
            written.Add(fullPath);
        }

        return new LlmsGenerationResult(written);
    }

    private static string GroupKey(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 ? segments[0] : "general";
    }

    private static string Excerpt(string text)
    {
        var normalized = string.Join(' ',
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= ExcerptLength ? normalized : normalized[..ExcerptLength];
    }

    private static string? FindTitle(DomNode root)
    {
        foreach (var node in EnumerateTag(root, "title"))
        {
            var text = node.TextContent.Trim();
            if (text.Length > 0)
            {
                return text;
            }
        }

        return null;
    }

    private static string? FindMetaDescription(DomNode root)
    {
        foreach (var meta in EnumerateTag(root, "meta"))
        {
            if (meta.Attributes.TryGetValue("name", out var name) &&
                string.Equals(name, "description", StringComparison.OrdinalIgnoreCase) &&
                meta.Attributes.TryGetValue("content", out var content) &&
                !string.IsNullOrWhiteSpace(content))
            {
                return content.Trim();
            }
        }

        return null;
    }

    private static string CollectVisibleText(DomNode node)
    {
        var builder = new StringBuilder();
        AppendVisible(node, builder);
        return builder.ToString();
    }

    private static void AppendVisible(DomNode node, StringBuilder builder)
    {
        if (node.TagName is "script" or "style" or "noscript")
        {
            return;
        }

        if (node.TextContent.Length > 0)
        {
            builder.Append(node.TextContent).Append(' ');
        }

        foreach (var child in node.Children)
        {
            AppendVisible(child, builder);
        }
    }

    private static IEnumerable<DomNode> EnumerateTag(DomNode node, string tag)
    {
        if (node.TagName == tag)
        {
            yield return node;
        }

        foreach (var child in node.Children)
        {
            foreach (var match in EnumerateTag(child, tag))
            {
                yield return match;
            }
        }
    }

    private async Task<IReadOnlyList<Candidate>> LoadCandidatesAsync(long auditId, CancellationToken cancellationToken)
    {
        var excluded = new HashSet<long>();
        var candidates = new List<Candidate>();
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken);

        await using (var errorCommand = connection.CreateCommand())
        {
            errorCommand.CommandText =
                "SELECT DISTINCT url_id FROM findings WHERE audit_id = @auditId AND severity = 0 AND url_id IS NOT NULL";
            AddParameter(errorCommand, "@auditId", auditId);
            await using var reader = await errorCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                excluded.Add(reader.GetInt64(0));
            }
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, path, canonical_url FROM url_records " +
            "WHERE audit_id = @auditId AND is_indexable = 1 AND status_class = 1 " +
            "AND (content_type IS NULL OR content_type LIKE '%html%') ORDER BY id";
        AddParameter(command, "@auditId", auditId);
        await using var candidateReader = await command.ExecuteReaderAsync(cancellationToken);
        while (await candidateReader.ReadAsync(cancellationToken) && candidates.Count < MaxCandidates)
        {
            var id = candidateReader.GetInt64(0);
            if (excluded.Contains(id))
            {
                continue;
            }

            candidates.Add(new Candidate(id, candidateReader.GetString(1), candidateReader.GetString(2)));
        }

        return candidates;
    }

    private async Task<string?> ReadStoredBodyAsync(long auditId, long urlId, CancellationToken cancellationToken)
    {
        string? storageKey;
        await using (var connection = await _provider.OpenConnectionAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT storage_key FROM content_artifacts WHERE audit_id = @auditId AND url_id = @urlId AND kind = 0";
            AddParameter(command, "@auditId", auditId);
            AddParameter(command, "@urlId", urlId);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            storageKey = result as string;
        }

        if (storageKey is null)
        {
            return null;
        }

        await using var stream = await _contentStore.OpenReadAsync(storageKey, cancellationToken);
        if (stream is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        var total = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            var allowed = Math.Min(read, MaxStoredBodyBytes - total);
            if (allowed <= 0)
            {
                break;
            }

            buffer.Write(chunk, 0, allowed);
            total += allowed;
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed record Candidate(long Id, string Path, string CanonicalUrl);

    private sealed record RankedPage(
        string Url,
        string Path,
        string Title,
        string? Description,
        string Text,
        int Words,
        double Composite);
}
