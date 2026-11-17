namespace Fixture.E7;

/// <summary>Bundled ONNX or user-configured loopback daemon only.</summary>
public sealed class LocalEmbeddingProvider
{
    public string DaemonEndpoint { get; set; } = "http://127.0.0.1:11434/api/embed";

    public bool ValidateEndpoint(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.IsLoopback;
}
