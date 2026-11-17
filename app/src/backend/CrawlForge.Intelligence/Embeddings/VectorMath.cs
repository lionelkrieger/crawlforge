namespace CrawlForge.Intelligence.Embeddings;

/// <summary>
/// Deterministic vector operations for the semantic passes (FS-CRAWL-002
/// §12.3–12.5): cosine similarity, mean pooling (the specified default for
/// multi-chunk documents), L2 normalization, and bounded top-K selection.
/// All operations are pure and allocation-bounded.
/// </summary>
public static class VectorMath
{
    /// <summary>
    /// Cosine similarity of two equal-length vectors, clamped to [-1, 1].
    /// Zero vectors yield 0 (no direction, no similarity).
    /// </summary>
    /// <param name="a">First vector.</param>
    /// <param name="b">Second vector.</param>
    /// <returns>Cosine similarity.</returns>
    /// <exception cref="ArgumentException">Dimensions differ.</exception>
    public static double CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException($"Vector dimensions differ: {a.Length} vs {b.Length}.");
        }

        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            normA += (double)a[i] * a[i];
            normB += (double)b[i] * b[i];
        }

        if (normA <= 0 || normB <= 0)
        {
            return 0;
        }

        var similarity = dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
        return Math.Clamp(similarity, -1.0, 1.0);
    }

    /// <summary>
    /// Mean pooling over chunk vectors (FS-CRAWL-002 §12.3 default), producing
    /// one document vector. Empty input yields an empty vector.
    /// </summary>
    /// <param name="vectors">Chunk vectors of equal dimension.</param>
    /// <returns>The mean-pooled vector.</returns>
    public static float[] MeanPool(IReadOnlyList<float[]> vectors)
    {
        if (vectors.Count == 0)
        {
            return [];
        }

        var dimension = vectors[0].Length;
        var pooled = new float[dimension];
        foreach (var vector in vectors)
        {
            if (vector.Length != dimension)
            {
                throw new ArgumentException("All chunk vectors must share one dimension.");
            }

            for (var i = 0; i < dimension; i++)
            {
                pooled[i] += vector[i];
            }
        }

        var scale = 1f / vectors.Count;
        for (var i = 0; i < dimension; i++)
        {
            pooled[i] *= scale;
        }

        return pooled;
    }

    /// <summary>
    /// Returns an L2-normalized copy; zero vectors are returned unchanged.
    /// </summary>
    /// <param name="vector">Input vector.</param>
    /// <returns>Unit-length copy (or zero vector).</returns>
    public static float[] Normalize(ReadOnlySpan<float> vector)
    {
        double norm = 0;
        for (var i = 0; i < vector.Length; i++)
        {
            norm += (double)vector[i] * vector[i];
        }

        var result = vector.ToArray();
        if (norm <= 0)
        {
            return result;
        }

        var scale = (float)(1.0 / Math.Sqrt(norm));
        for (var i = 0; i < result.Length; i++)
        {
            result[i] *= scale;
        }

        return result;
    }

    /// <summary>
    /// Selects the top-K entries by descending score (stable by index for ties).
    /// Excess entries are discarded — the §7.9 bound philosophy.
    /// </summary>
    /// <typeparam name="T">Entry type.</typeparam>
    /// <param name="entries">Candidate entries.</param>
    /// <param name="score">Score selector.</param>
    /// <param name="topK">Maximum entries to retain.</param>
    /// <returns>At most <paramref name="topK"/> entries, highest score first.</returns>
    public static IReadOnlyList<T> TopK<T>(IEnumerable<T> entries, Func<T, double> score, int topK)
    {
        if (topK <= 0)
        {
            return [];
        }

        return entries
            .Select((entry, index) => (entry, index, value: score(entry)))
            .OrderByDescending(item => item.value)
            .ThenBy(item => item.index)
            .Take(topK)
            .Select(item => item.entry)
            .ToArray();
    }
}
