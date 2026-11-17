using CrawlForge.Contracts.Intelligence;
using CrawlForge.Intelligence.Embeddings;
using Xunit;

namespace CrawlForge.Intelligence.Tests.Embeddings;

/// <summary>
/// Embedding payload serialization (model id, dimension, content hash, vector
/// blob) and malformed-payload tolerance.
/// </summary>
public class EmbeddingPayloadTests
{
    [Fact]
    public void RoundTripPreservesAllFields()
    {
        var payload = new EmbeddingPayload("onnx:test:4", 4, "hash-123", [0.1f, 0.2f, 0.3f, 0.4f]);
        var parsed = EmbeddingPayload.TryParse(payload.ToJson());

        Assert.NotNull(parsed);
        Assert.Equal("onnx:test:4", parsed.ModelTag);
        Assert.Equal(4, parsed.Dimensions);
        Assert.Equal("hash-123", parsed.ContentHash);
        Assert.Equal(payload.Values, parsed.Values);
    }

    [Fact]
    public void FromVectorRecordsModelAndDimension()
    {
        var vector = new EmbeddingVector(new float[] { 1, 0, 0 }, "fake:model:3");
        var payload = EmbeddingPayload.FromVector(vector, "content-hash");

        Assert.Equal("fake:model:3", payload.ModelTag);
        Assert.Equal(3, payload.Dimensions);
        Assert.Equal("content-hash", payload.ContentHash);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"modelTag\":\"m\",\"dimensions\":9,\"values\":[1,2]}")] // dimension mismatch
    [InlineData("{\"modelTag\":\"\",\"dimensions\":1,\"values\":[1]}")]    // empty tag
    [InlineData("{\"modelTag\":\"m\",\"dimensions\":0,\"values\":[]}")]    // empty vector
    public void MalformedPayloadsReturnNull(string? json)
    {
        Assert.Null(EmbeddingPayload.TryParse(json));
    }

    [Fact]
    public void PayloadJsonIsCamelCase()
    {
        var payload = new EmbeddingPayload("m", 1, "h", [1f]);
        var json = payload.ToJson();
        Assert.Contains("\"modelTag\"", json, StringComparison.Ordinal);
        Assert.Contains("\"contentHash\"", json, StringComparison.Ordinal);
        Assert.Contains("\"dimensions\"", json, StringComparison.Ordinal);
    }
}
