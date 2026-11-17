using System.Text;

namespace CrawlForge.Intelligence.Embeddings;

/// <summary>
/// Bounded, deterministic chunking and input assembly (FS-CRAWL-002 §12.3):
/// the embedding input is Title + H1 + scoped body through a fixed template;
/// long documents produce multiple bounded chunks with optional overlap whose
/// vectors are mean-pooled (the specified default). Chunking never crosses the
/// configured maximum length and is stable across runs.
/// </summary>
public sealed class TextChunker
{
    /// <summary>Default maximum chunk length in characters (~256 WordPiece tokens).</summary>
    public const int DefaultMaxChunkChars = 1200;

    /// <summary>Default chunk overlap in characters.</summary>
    public const int DefaultOverlapChars = 128;

    private readonly int _maxChunkChars;
    private readonly int _overlapChars;

    /// <summary>Creates the chunker with bounded chunk size and overlap.</summary>
    /// <param name="maxChunkChars">Maximum chunk length (minimum 64).</param>
    /// <param name="overlapChars">Overlap between consecutive chunks (0 ≤ overlap &lt; max).</param>
    public TextChunker(int maxChunkChars = DefaultMaxChunkChars, int overlapChars = DefaultOverlapChars)
    {
        if (maxChunkChars < 64)
        {
            throw new ArgumentOutOfRangeException(nameof(maxChunkChars), "Chunk length must be at least 64 characters.");
        }

        if (overlapChars < 0 || overlapChars >= maxChunkChars)
        {
            throw new ArgumentOutOfRangeException(nameof(overlapChars), "Overlap must be in [0, maxChunkChars).");
        }

        _maxChunkChars = maxChunkChars;
        _overlapChars = overlapChars;
    }

    /// <summary>
    /// Assembles the deterministic embedding input template: title, then H1,
    /// then scoped body (FS-CRAWL-002 §12.3). Empty parts are omitted.
    /// </summary>
    /// <param name="title">Full document title (never scoped — head fact).</param>
    /// <param name="h1">First H1 text within the scoped tree.</param>
    /// <param name="scopedBody">Scoped visible body text.</param>
    /// <returns>The assembled input text.</returns>
    public static string AssembleInput(string? title, string? h1, string? scopedBody)
    {
        var builder = new StringBuilder();
        AppendPart(builder, title);
        AppendPart(builder, h1);
        AppendPart(builder, scopedBody);
        return builder.ToString();
    }

    /// <summary>
    /// Splits text into bounded chunks with overlap, preferring word boundaries.
    /// Empty or whitespace input yields no chunks.
    /// </summary>
    /// <param name="text">Input text.</param>
    /// <returns>Ordered, bounded chunks.</returns>
    public IReadOnlyList<string> Chunk(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var normalized = NormalizeWhitespace(text);
        if (normalized.Length <= _maxChunkChars)
        {
            return [normalized];
        }

        var chunks = new List<string>();
        var start = 0;
        while (start < normalized.Length)
        {
            var end = Math.Min(start + _maxChunkChars, normalized.Length);
            if (end < normalized.Length)
            {
                // Prefer a word boundary inside the last quarter of the window.
                var boundary = normalized.LastIndexOf(' ', end - 1, Math.Min(end - start, _maxChunkChars / 4));
                if (boundary > start)
                {
                    end = boundary;
                }
            }

            var chunk = normalized[start..end].Trim();
            if (chunk.Length > 0)
            {
                chunks.Add(chunk);
            }

            if (end >= normalized.Length)
            {
                break;
            }

            var next = end - _overlapChars;
            start = next > start ? next : end; // guarantee forward progress
        }

        return chunks;
    }

    private static void AppendPart(StringBuilder builder, string? part)
    {
        if (string.IsNullOrWhiteSpace(part))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append('\n');
        }

        builder.Append(part.Trim());
    }

    private static string NormalizeWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
            }
            else
            {
                if (pendingSpace)
                {
                    builder.Append(' ');
                    pendingSpace = false;
                }

                builder.Append(ch);
            }
        }

        return builder.ToString();
    }
}
