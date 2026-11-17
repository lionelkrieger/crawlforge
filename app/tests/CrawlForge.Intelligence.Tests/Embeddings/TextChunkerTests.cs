using CrawlForge.Intelligence.Embeddings;
using Xunit;

namespace CrawlForge.Intelligence.Tests.Embeddings;

/// <summary>
/// Bounded deterministic chunking and the §12.3 input template
/// (title, H1, then scoped body).
/// </summary>
public class TextChunkerTests
{
    [Fact]
    public void ShortTextProducesOneChunk()
    {
        var chunker = new TextChunker();
        var chunks = chunker.Chunk("A short paragraph about semantic linking.");
        Assert.Single(chunks);
        Assert.Equal("A short paragraph about semantic linking.", chunks[0]);
    }

    [Fact]
    public void EmptyInputProducesNoChunks()
    {
        var chunker = new TextChunker();
        Assert.Empty(chunker.Chunk(null));
        Assert.Empty(chunker.Chunk("   "));
    }

    [Fact]
    public void LongTextIsBoundedWithOverlap()
    {
        var chunker = new TextChunker(maxChunkChars: 100, overlapChars: 20);
        var text = string.Join(' ', Enumerable.Repeat("alpha beta gamma delta", 40));
        var chunks = chunker.Chunk(text);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= 100));

        // Consecutive chunks share overlap content.
        var tail = chunks[0][^20..];
        Assert.Contains(tail.Split(' ')[0], chunks[1], StringComparison.Ordinal);
    }

    [Fact]
    public void ChunkingIsDeterministic()
    {
        var chunker = new TextChunker(maxChunkChars: 128, overlapChars: 16);
        var text = string.Join(' ', Enumerable.Repeat("the quick brown fox jumps", 60));
        Assert.Equal(chunker.Chunk(text), chunker.Chunk(text));
    }

    [Fact]
    public void AllContentIsCovered()
    {
        var chunker = new TextChunker(maxChunkChars: 100, overlapChars: 20);
        var words = Enumerable.Range(0, 200).Select(i => "word" + i).ToArray();
        var chunks = chunker.Chunk(string.Join(' ', words));
        var joined = string.Join(' ', chunks);
        foreach (var word in words)
        {
            Assert.Contains(word, joined, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void InputTemplateOrdersTitleH1Body()
    {
        var input = TextChunker.AssembleInput("My Title", "My Heading", "Body text here.");
        Assert.Equal("My Title\nMy Heading\nBody text here.", input);
    }

    [Fact]
    public void InputTemplateOmitsEmptyParts()
    {
        Assert.Equal("Body only.", TextChunker.AssembleInput(null, "  ", "Body only."));
        Assert.Equal("Title\nBody.", TextChunker.AssembleInput("Title", null, "Body."));
        Assert.Equal(string.Empty, TextChunker.AssembleInput(null, null, null));
    }

    [Fact]
    public void InvalidBoundsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextChunker(maxChunkChars: 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextChunker(maxChunkChars: 100, overlapChars: 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TextChunker(maxChunkChars: 100, overlapChars: -1));
    }

    [Fact]
    public void WhitespaceIsNormalized()
    {
        var chunker = new TextChunker();
        var chunks = chunker.Chunk("  lots   of\twhitespace\nhere  ");
        Assert.Equal("lots of whitespace here", chunks[0]);
    }
}
