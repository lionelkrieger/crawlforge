using CrawlForge.Intelligence.Embeddings;
using Xunit;

namespace CrawlForge.Intelligence.Tests.Embeddings;

/// <summary>
/// Cosine similarity, mean pooling, normalization, and bounded top-K
/// (FS-CRAWL-002 §12.3–12.5).
/// </summary>
public class VectorMathTests
{
    [Fact]
    public void CosineOfIdenticalDirectionsIsOne()
    {
        Assert.Equal(1.0, VectorMath.CosineSimilarity(new float[] { 1, 0, 0 }, new float[] { 2, 0, 0 }), 5);
    }

    [Fact]
    public void CosineOfOrthogonalVectorsIsZero()
    {
        Assert.Equal(0.0, VectorMath.CosineSimilarity(new float[] { 1, 0 }, new float[] { 0, 1 }), 5);
    }

    [Fact]
    public void CosineOfOppositeDirectionsIsMinusOne()
    {
        Assert.Equal(-1.0, VectorMath.CosineSimilarity(new float[] { 1, 0 }, new float[] { -1, 0 }), 5);
    }

    [Fact]
    public void CosineOfZeroVectorIsZero()
    {
        Assert.Equal(0.0, VectorMath.CosineSimilarity(new float[] { 0, 0 }, new float[] { 1, 1 }), 5);
    }

    [Fact]
    public void CosineRequiresEqualDimensions()
    {
        Assert.Throws<ArgumentException>(() =>
            VectorMath.CosineSimilarity(new float[] { 1 }, new float[] { 1, 2 }));
    }

    [Fact]
    public void KnownCosineValue()
    {
        var a = new float[] { 1, 0 };
        var b = VectorMath.Normalize(new float[] { 0.85f, 0.5268f });
        Assert.Equal(0.85, VectorMath.CosineSimilarity(a, b), 2);
    }

    [Fact]
    public void MeanPoolAveragesChunkVectors()
    {
        var pooled = VectorMath.MeanPool([new float[] { 1, 0 }, new float[] { 0, 1 }]);
        Assert.Equal(new float[] { 0.5f, 0.5f }, pooled);
    }

    [Fact]
    public void MeanPoolOfEmptyIsEmpty()
    {
        Assert.Empty(VectorMath.MeanPool([]));
    }

    [Fact]
    public void MeanPoolRequiresUniformDimension()
    {
        Assert.Throws<ArgumentException>(() =>
            VectorMath.MeanPool([new float[] { 1 }, new float[] { 1, 2 }]));
    }

    [Fact]
    public void NormalizeProducesUnitLength()
    {
        var normalized = VectorMath.Normalize(new float[] { 3, 4 });
        Assert.Equal(0.6f, normalized[0], 5);
        Assert.Equal(0.8f, normalized[1], 5);
    }

    [Fact]
    public void NormalizeLeavesZeroVectorUnchanged()
    {
        Assert.Equal(new float[] { 0, 0 }, VectorMath.Normalize(new float[] { 0, 0 }));
    }

    [Fact]
    public void TopKSelectsHighestScoresAndBounds()
    {
        var entries = new[] { ("a", 0.5), ("b", 0.9), ("c", 0.7), ("d", 0.1) };
        var top = VectorMath.TopK(entries, e => e.Item2, 2);
        Assert.Equal(2, top.Count);
        Assert.Equal("b", top[0].Item1);
        Assert.Equal("c", top[1].Item1);
    }

    [Fact]
    public void TopKWithZeroOrNegativeBoundIsEmpty()
    {
        Assert.Empty(VectorMath.TopK(new[] { 1, 2, 3 }, x => x, 0));
    }

    [Fact]
    public void TopKTiesAreStableByIndex()
    {
        var entries = new[] { "first", "second", "third" };
        var top = VectorMath.TopK(entries, _ => 1.0, 2);
        Assert.Equal(new[] { "first", "second" }, top);
    }
}
