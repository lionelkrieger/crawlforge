using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CrawlForge.Contracts.Intelligence;

namespace CrawlForge.Intelligence.Embeddings;

/// <summary>
/// Local daemon API dialects (all loopback-only, user-owned infrastructure).
/// Auto probes the Ollama embed endpoint first, then the legacy Ollama
/// embeddings endpoint, then the OpenAI-compatible /v1/embeddings shape used
/// by local daemons such as LM Studio.
/// </summary>
public enum LocalDaemonApiStyle
{
    /// <summary>Probe dialects in order and remember the first that works.</summary>
    Auto = 0,

    /// <summary>POST /api/embed (Ollama).</summary>
    OllamaEmbed = 1,

    /// <summary>POST /api/embeddings (legacy Ollama).</summary>
    OllamaLegacy = 2,

    /// <summary>POST /v1/embeddings (OpenAI-compatible local daemon).</summary>
    V1Embeddings = 3,
}

/// <summary>
/// User-configured LOOPBACK-ONLY embedding daemon provider (FS-CRAWL-002
/// §12.2, exclusion E7): talks to an Ollama / LM Studio-class daemon on the
/// local machine. The endpoint is validated at configuration time and again
/// before every request; connections are pinned to the loopback IP WITHOUT
/// DNS so daemon traffic can never leave the machine. No API keys, no cloud
/// fallback, no remote service of any kind.
/// </summary>
public sealed class LoopbackDaemonEmbeddingProvider : ILocalEmbeddingProvider, IDisposable
{
    /// <summary>Default daemon model tag (common local embedding model).</summary>
    public const string DefaultDaemonModelTag = "nomic-embed-text";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly Uri _endpoint;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly TimeSpan _timeout;
    private readonly LocalDaemonApiStyle _configuredStyle;
    private LocalDaemonApiStyle _resolvedStyle;
    private int _probedDimensions;
    private bool _disposed;

    /// <summary>Creates the provider; the endpoint is validated loopback-only.</summary>
    /// <param name="endpoint">User-configured loopback endpoint.</param>
    /// <param name="modelTag">Daemon model tag; null uses the default.</param>
    /// <param name="httpClient">Optional injected client (tests); otherwise a loopback-pinned client is created.</param>
    /// <param name="timeout">Per-request timeout.</param>
    /// <param name="style">API dialect; Auto probes.</param>
    /// <exception cref="EmbeddingConfigurationException">The endpoint is non-loopback or malformed.</exception>
    public LoopbackDaemonEmbeddingProvider(
        string? endpoint,
        string? modelTag = null,
        HttpClient? httpClient = null,
        TimeSpan? timeout = null,
        LocalDaemonApiStyle style = LocalDaemonApiStyle.Auto)
    {
        _endpoint = LoopbackEndpointValidator.Validate(endpoint);
        ModelTag = string.IsNullOrWhiteSpace(modelTag) ? DefaultDaemonModelTag : modelTag;
        _timeout = timeout ?? DefaultTimeout;
        _configuredStyle = style;
        _resolvedStyle = style;

        if (httpClient is not null)
        {
            _http = httpClient;
            _ownsHttp = false;
        }
        else
        {
            _http = new HttpClient(CreateLoopbackPinnedHandler()) { Timeout = Timeout.InfiniteTimeSpan };
            _ownsHttp = true;
        }
    }

    /// <inheritdoc />
    public string ModelTag { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Daemon dimensions are model-dependent: the value is probed on the first
    /// successful embed or connection test (0 before any probe).
    /// </remarks>
    public int Dimensions => Volatile.Read(ref _probedDimensions);

    /// <inheritdoc />
    public async Task<EmbeddingVector> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Defense in depth: re-validate before every request (§12.2).
        LoopbackEndpointValidator.Validate(_endpoint.AbsoluteUri);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);

        var styles = _resolvedStyle == LocalDaemonApiStyle.Auto
            ? new[] { LocalDaemonApiStyle.OllamaEmbed, LocalDaemonApiStyle.OllamaLegacy, LocalDaemonApiStyle.V1Embeddings }
            : new[] { _resolvedStyle };

        Exception? lastError = null;
        foreach (var style in styles)
        {
            timeoutSource.Token.ThrowIfCancellationRequested();
            try
            {
                var vector = await EmbedWithStyleAsync(style, text, timeoutSource.Token);
                if (_configuredStyle == LocalDaemonApiStyle.Auto)
                {
                    _resolvedStyle = style;
                }

                Volatile.Write(ref _probedDimensions, vector.Length);
                return new EmbeddingVector(VectorMath.Normalize(vector), ModelTag);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound && _resolvedStyle == LocalDaemonApiStyle.Auto)
            {
                lastError = ex; // dialect not present on this daemon; probe the next
            }
        }

        throw new EmbeddingConfigurationException(
            $"The loopback daemon at {_endpoint} did not accept any supported embedding dialect. {lastError?.Message}");
    }

    /// <inheritdoc />
    public async Task<EmbeddingProviderTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            LoopbackEndpointValidator.Validate(_endpoint.AbsoluteUri);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(_timeout);

            var reachable = await ProbeReachabilityAsync(timeoutSource.Token);
            if (!reachable)
            {
                return new EmbeddingProviderTestResult(false, false, $"Loopback daemon at {_endpoint} is not reachable.");
            }

            var probe = await EmbedAsync("connection test", cancellationToken);
            return new EmbeddingProviderTestResult(
                true,
                true,
                $"Loopback daemon reachable; model '{ModelTag}' produced {probe.Values.Length} dimensions. Loopback-only, no remote service.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new EmbeddingProviderTestResult(false, false, $"Loopback daemon test failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    private async Task<float[]> EmbedWithStyleAsync(LocalDaemonApiStyle style, string text, CancellationToken cancellationToken)
    {
        var (path, payload) = style switch
        {
            LocalDaemonApiStyle.OllamaEmbed => ("/api/embed", JsonSerializer.Serialize(new { model = ModelTag, input = new[] { text } })),
            LocalDaemonApiStyle.OllamaLegacy => ("/api/embeddings", JsonSerializer.Serialize(new { model = ModelTag, prompt = text })),
            _ => ("/v1/embeddings", JsonSerializer.Serialize(new { model = ModelTag, input = text })),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_endpoint, path))
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;

        JsonElement vectorElement = style switch
        {
            LocalDaemonApiStyle.OllamaEmbed => root.GetProperty("embeddings")[0],
            LocalDaemonApiStyle.OllamaLegacy => root.GetProperty("embedding"),
            _ => root.GetProperty("data")[0].GetProperty("embedding"),
        };

        var vector = new float[vectorElement.GetArrayLength()];
        var index = 0;
        foreach (var component in vectorElement.EnumerateArray())
        {
            vector[index++] = component.GetSingle();
        }

        if (vector.Length == 0)
        {
            throw new EmbeddingConfigurationException($"The loopback daemon returned an empty vector for model '{ModelTag}'.");
        }

        return vector;
    }

    private async Task<bool> ProbeReachabilityAsync(CancellationToken cancellationToken)
    {
        foreach (var path in new[] { "/api/tags", "/v1/models" })
        {
            try
            {
                using var response = await _http.GetAsync(new Uri(_endpoint, path), cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch (HttpRequestException)
            {
                // Try the next dialect's discovery endpoint.
            }
        }

        return false;
    }

    /// <summary>
    /// Creates a handler whose connections are pinned to the loopback IP with
    /// NO DNS resolution: even a poisoned hosts file cannot redirect daemon
    /// traffic off-machine (criterion #45, §1.4 rule 9).
    /// </summary>
    /// <returns>A loopback-pinned sockets handler.</returns>
    public static SocketsHttpHandler CreateLoopbackPinnedHandler() => new()
    {
        ConnectCallback = (context, cancellationToken) =>
            new ValueTask<Stream>(OpenLoopbackConnectionAsync(context.DnsEndPoint, cancellationToken)),
    };

    /// <summary>
    /// Opens a connection pinned to the loopback IP without DNS. Non-loopback
    /// hosts are refused before any socket is opened (exclusion E7).
    /// </summary>
    /// <param name="endpoint">The requested DNS endpoint (host must be loopback).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A network stream over the loopback socket.</returns>
    /// <exception cref="EmbeddingConfigurationException">The host is not loopback.</exception>
    public static async Task<Stream> OpenLoopbackConnectionAsync(DnsEndPoint endpoint, CancellationToken cancellationToken)
    {
        var host = endpoint.Host;
        if (!LoopbackEndpointValidator.IsLoopbackHost(host))
        {
            throw new EmbeddingConfigurationException($"Refusing non-loopback connection to '{host}' (exclusion E7).");
        }

        var address = LoopbackEndpointValidator.ResolveLoopbackAddress(new Uri($"http://{host}"));
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
