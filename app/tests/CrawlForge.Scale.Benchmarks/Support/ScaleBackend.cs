using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CrawlForge.Host;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CrawlForge.Scale.Benchmarks.Support;

/// <summary>
/// In-process handle over the REAL product composition root
/// (<see cref="CrawlForgeBackend"/>): loopback Kestrel, session gate, migrated
/// SQLite persistence, hub, metrics, and diagnostics — the same stack the
/// Electron shell supervises. Benchmarks drive it over loopback HTTP exactly
/// like the renderer does, and resolve product services for direct proofs.
/// </summary>
public sealed class ScaleBackend : IAsyncDisposable
{
    /// <summary>Wire JSON options (camelCase, matching the product API).</summary>
    public static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    private ScaleBackend(CrawlForgeBackend backend, HttpClient http)
    {
        Backend = backend;
        Http = http;
    }

    /// <summary>The live product backend.</summary>
    public CrawlForgeBackend Backend { get; }

    /// <summary>Session-authenticated loopback client.</summary>
    public HttpClient Http { get; }

    /// <summary>Product service provider (repositories, engines, diagnostics).</summary>
    public IServiceProvider Services => Backend.App.Services;

    /// <summary>Loopback origin the backend bound.</summary>
    public string BaseAddress => Backend.BaseAddress;

    /// <summary>Per-launch session token (loopback handshake).</summary>
    public string SessionToken => Backend.SessionToken;

    /// <summary>Starts the real backend against an isolated data directory.</summary>
    /// <param name="dataDirectory">Per-run data directory (database, content store, diagnostics).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A started backend handle.</returns>
    public static async Task<ScaleBackend> StartAsync(string dataDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(dataDirectory);
        var backend = await CrawlForgeBackend.StartAsync(
            args: null,
            configureBuilder: builder => builder.Configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["CrawlForge:DataDirectory"] = dataDirectory,
                    ["CrawlForge:Port"] = "0"
                }),
            cancellationToken: cancellationToken);

        var http = new HttpClient { BaseAddress = new Uri(backend.BaseAddress), Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.Add("X-CrawlForge-Session", backend.SessionToken);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return new ScaleBackend(backend, http);
    }

    /// <summary>Resolves a product service.</summary>
    /// <typeparam name="T">Service type.</typeparam>
    /// <returns>The registered service instance.</returns>
    public T GetRequired<T>() where T : notnull => Services.GetRequiredService<T>();

    /// <summary>GETs an enveloped route and returns its data payload.</summary>
    /// <typeparam name="T">Expected data payload type.</typeparam>
    /// <param name="pathAndQuery">Absolute path + query (e.g. /api/system/health).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The deserialized data payload.</returns>
    public async Task<T> GetDataAsync<T>(string pathAndQuery, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(pathAndQuery, cancellationToken);
        return ExtractData<T>(document);
    }

    /// <summary>GETs a route and returns the raw JSON document.</summary>
    /// <param name="pathAndQuery">Absolute path + query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The parsed response document.</returns>
    public async Task<JsonDocument> GetJsonAsync(string pathAndQuery, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(pathAndQuery, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GET {pathAndQuery} → {(int)response.StatusCode}: {body}");
        }
        return JsonDocument.Parse(body);
    }

    /// <summary>GETs a non-JSON surface (e.g. /metrics exposition text).</summary>
    /// <param name="pathAndQuery">Absolute path + query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The raw response body.</returns>
    public async Task<string> GetTextAsync(string pathAndQuery, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(pathAndQuery, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GET {pathAndQuery} → {(int)response.StatusCode}: {body}");
        }
        return body;
    }

    /// <summary>POSTs an enveloped route and returns its data payload.</summary>
    /// <typeparam name="T">Expected data payload type.</typeparam>
    /// <param name="path">Absolute path.</param>
    /// <param name="payload">JSON body, or null for an empty body.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The deserialized data payload.</returns>
    public async Task<T> PostDataAsync<T>(string path, object? payload, CancellationToken cancellationToken)
    {
        using var content = new StringContent(
            payload is null ? "{}" : JsonSerializer.Serialize(payload, WireJson),
            Encoding.UTF8,
            "application/json");
        using var response = await Http.PostAsync(path, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"POST {path} → {(int)response.StatusCode}: {body}");
        }
        using var document = JsonDocument.Parse(body);
        return ExtractData<T>(document);
    }

    /// <summary>Writes a measurement fragment under artifacts/results.</summary>
    /// <param name="name">Fragment file name (without extension).</param>
    /// <param name="payload">Serializable measurement record.</param>
    public static void WriteResult(string name, object payload)
    {
        ScalePaths.EnsureArtifacts();
        var path = Path.Combine(ScalePaths.ResultsDirectory, name + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        }));
    }

    private static T ExtractData<T>(JsonDocument document)
    {
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
        {
            throw new InvalidOperationException("API envelope error: " + error.GetRawText());
        }
        var data = root.GetProperty("data");
        return data.Deserialize<T>(WireJson)
            ?? throw new InvalidOperationException($"Envelope data did not deserialize to {typeof(T).Name}.");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await Backend.DisposeAsync();
    }
}
