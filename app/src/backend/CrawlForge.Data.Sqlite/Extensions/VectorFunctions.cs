using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CrawlForge.Data.Sqlite.Extensions;

/// <summary>
/// Vector-similarity capability family (FS-CRAWL-001 §2.4: vector-similarity search
/// over stored embeddings; schema catalog: <c>url_embeddings.embedding payload</c>).
/// Registers <c>vec_dim</c>, <c>vec_norm</c>, <c>vec_dot</c>, <c>vec_distance</c>
/// (Euclidean/L2), and <c>vec_cosine</c> (cosine similarity). Vectors are stored either
/// as compact BLOBs of little-endian float32 components or as JSON numeric arrays;
/// both encodings are accepted by every function. All computation is local math —
/// no remote embedding or vector service is contacted (exclusion E7).
/// NULL inputs, malformed payloads, and dimension mismatches yield SQL NULL;
/// cosine similarity of a zero vector yields SQL NULL.
/// </summary>
internal static class VectorFunctions
{
    /// <summary>Registers the vector-similarity family on an open connection.</summary>
    /// <param name="connection">The connection to extend.</param>
    public static void Register(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        connection.CreateFunction(
            "vec_dim",
            (object? vector) => (long?)ParseVector(vector)?.Length,
            isDeterministic: true);

        connection.CreateFunction(
            "vec_norm",
            (object? vector) => Norm(ParseVector(vector)),
            isDeterministic: true);

        connection.CreateFunction(
            "vec_dot",
            (object? left, object? right) =>
            {
                var a = ParseVector(left);
                var b = ParseVector(right);
                if (a is null || b is null || a.Length != b.Length)
                {
                    return null;
                }

                var sum = 0.0;
                for (var i = 0; i < a.Length; i++)
                {
                    sum += a[i] * b[i];
                }

                return (double?)sum;
            },
            isDeterministic: true);

        connection.CreateFunction(
            "vec_distance",
            (object? left, object? right) =>
            {
                var a = ParseVector(left);
                var b = ParseVector(right);
                if (a is null || b is null || a.Length != b.Length)
                {
                    return null;
                }

                var sum = 0.0;
                for (var i = 0; i < a.Length; i++)
                {
                    var delta = a[i] - b[i];
                    sum += delta * delta;
                }

                return (double?)Math.Sqrt(sum);
            },
            isDeterministic: true);

        connection.CreateFunction(
            "vec_cosine",
            (object? left, object? right) =>
            {
                var a = ParseVector(left);
                var b = ParseVector(right);
                if (a is null || b is null || a.Length != b.Length)
                {
                    return null;
                }

                var dot = 0.0;
                var normA = 0.0;
                var normB = 0.0;
                for (var i = 0; i < a.Length; i++)
                {
                    dot += a[i] * b[i];
                    normA += a[i] * a[i];
                    normB += b[i] * b[i];
                }

                if (normA == 0.0 || normB == 0.0)
                {
                    return null;
                }

                return (double?)(dot / (Math.Sqrt(normA) * Math.Sqrt(normB)));
            },
            isDeterministic: true);
    }

    private static double? Norm(double[]? vector)
    {
        if (vector is null)
        {
            return null;
        }

        var sum = 0.0;
        foreach (var component in vector)
        {
            sum += component * component;
        }

        return Math.Sqrt(sum);
    }

    private static double[]? ParseVector(object? value) => value switch
    {
        byte[] blob => ParseBlob(blob),
        string text => ParseJsonArray(text),
        _ => null,
    };

    private static double[]? ParseBlob(byte[] blob)
    {
        if (blob.Length == 0 || blob.Length % sizeof(float) != 0)
        {
            return null;
        }

        var components = new double[blob.Length / sizeof(float)];
        for (var i = 0; i < components.Length; i++)
        {
            components[i] = BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(i * sizeof(float), sizeof(float)));
        }

        return components;
    }

    private static double[]? ParseJsonArray(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var components = new List<double>(document.RootElement.GetArrayLength());
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Number)
                {
                    return null;
                }

                components.Add(element.GetDouble());
            }

            return components.Count == 0 ? null : [.. components];
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
