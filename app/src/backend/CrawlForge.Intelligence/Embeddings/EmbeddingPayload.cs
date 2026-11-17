using System.Text.Json;
using CrawlForge.Contracts.Intelligence;

namespace CrawlForge.Intelligence.Embeddings;

/// <summary>
/// Serialized embedding payload stored in url_embeddings.embedding_payload_json
/// (FS-CRAWL-002 §12.4): the provider's vector serialization carrying the model
/// id, dimension, producing content hash (incremental re-embedding key, §12.3),
/// and the vector blob. JSON float array keeps both database providers
/// provider-agnostic (precedence rule 6).
/// </summary>
/// <param name="ModelTag">Provider + model + dimension tag.</param>
/// <param name="Dimensions">Vector dimensionality.</param>
/// <param name="ContentHash">Content hash the vector was produced from (cache key).</param>
/// <param name="Values">Vector components.</param>
public sealed record EmbeddingPayload(
    string ModelTag,
    int Dimensions,
    string? ContentHash,
    float[] Values)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Serializes the payload for persistence.</summary>
    /// <returns>JSON payload text.</returns>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// Parses a stored payload; malformed or legacy payloads return null
    /// (the URL is then re-embedded — never fatal).
    /// </summary>
    /// <param name="json">Stored payload text.</param>
    /// <returns>The parsed payload, or null.</returns>
    public static EmbeddingPayload? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<EmbeddingPayload>(json, JsonOptions);
            if (payload is null || payload.Values is null || payload.Values.Length == 0 ||
                payload.Dimensions != payload.Values.Length || string.IsNullOrEmpty(payload.ModelTag))
            {
                return null;
            }

            return payload;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Creates a payload from a provider vector and content hash.</summary>
    /// <param name="vector">Provider vector with model tag.</param>
    /// <param name="contentHash">Content hash the vector represents.</param>
    /// <returns>The persistable payload.</returns>
    public static EmbeddingPayload FromVector(EmbeddingVector vector, string? contentHash) =>
        new(vector.ModelTag, vector.Values.Length, contentHash, vector.Values.ToArray());
}
