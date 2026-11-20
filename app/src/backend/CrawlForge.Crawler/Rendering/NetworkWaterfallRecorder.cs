using System.Diagnostics;
using System.Text.Json;
using CrawlForge.Contracts.Crawl;

namespace CrawlForge.Crawler.Rendering;

/// <summary>
/// Reduces CDP Network-domain events into the locked rendered-capture
/// waterfall (FS-CRAWL-001 §5.6.2, CR-006; <see cref="RenderedNetworkEntry"/>).
/// Rendered traffic is captured DIRECTLY here — the loopback intercepting
/// proxy is not used for rendered traffic (§5.9 as amended by CR-006).
/// <para>
/// Also maintains the network side of the wait rule (§5.6.2, CR-009): the
/// in-flight non-streaming request count and the last network-activity time.
/// Long-lived connections (websockets, server-sent events, long-polling and
/// other streaming responses) are excluded from the quiet-network test.
/// </para>
/// </summary>
public sealed class NetworkWaterfallRecorder
{
    private static readonly HashSet<string> StreamingMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/event-stream",
        "application/stream+json",
        "application/x-ndjson",
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, EntryState> _byRequestId = new(StringComparer.Ordinal);
    private readonly List<EntryState> _order = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly int _maxEntries;
    private readonly long _streamingPendingThresholdMs;
    private long _lastActivityMs;

    /// <summary>Creates the recorder.</summary>
    /// <param name="maxEntries">Soft bound on retained waterfall entries.</param>
    /// <param name="streamingPendingThresholdMs">
    /// Age after which a response-less pending request is treated as
    /// long-polling (excluded from the quiet test). Implementation heuristic
    /// for the spec's "long-polling and other streaming requests" class.
    /// </param>
    public NetworkWaterfallRecorder(int maxEntries = 5000, long streamingPendingThresholdMs = 3000)
    {
        _maxEntries = Math.Max(1, maxEntries);
        _streamingPendingThresholdMs = Math.Max(1, streamingPendingThresholdMs);
        _lastActivityMs = _clock.ElapsedMilliseconds;
    }

    /// <summary>Milliseconds since the last non-streaming network activity.</summary>
    public long LastActivityAgeMs
    {
        get
        {
            lock (_gate)
            {
                return _clock.ElapsedMilliseconds - _lastActivityMs;
            }
        }
    }

    /// <summary>Number of in-flight requests that are NOT streaming (§5.6.2 quiet test).</summary>
    public int InFlightNonStreamingCount
    {
        get
        {
            lock (_gate)
            {
                var now = _clock.ElapsedMilliseconds;
                var count = 0;
                foreach (var entry in _order)
                {
                    if (!entry.Completed && !IsStreaming(entry, now))
                    {
                        count++;
                    }
                }

                return count;
            }
        }
    }

    /// <summary>Feeds one CDP event (non-Network events are ignored).</summary>
    /// <param name="cdpEvent">The event.</param>
    public void OnEvent(CdpEvent cdpEvent)
    {
        ArgumentNullException.ThrowIfNull(cdpEvent);
        if (!cdpEvent.Method.StartsWith("Network.", StringComparison.Ordinal))
        {
            return;
        }

        lock (_gate)
        {
            switch (cdpEvent.Method)
            {
                case "Network.requestWillBeSent":
                    OnRequestWillBeSent(cdpEvent.Params);
                    break;
                case "Network.responseReceived":
                    OnResponseReceived(cdpEvent.Params);
                    break;
                case "Network.dataReceived":
                    OnDataReceived(cdpEvent.Params);
                    break;
                case "Network.loadingFinished":
                    OnLoadingFinished(cdpEvent.Params);
                    break;
                case "Network.loadingFailed":
                    OnLoadingFailed(cdpEvent.Params);
                    break;
                case "Network.requestServedFromCache":
                    OnRequestServedFromCache(cdpEvent.Params);
                    break;
            }
        }
    }

    /// <summary>Builds the waterfall entries in observation order.</summary>
    /// <returns>The locked-shape entries.</returns>
    public IReadOnlyList<RenderedNetworkEntry> GetEntries()
    {
        lock (_gate)
        {
            var entries = new List<RenderedNetworkEntry>(_order.Count);
            foreach (var state in _order)
            {
                entries.Add(state.ToEntry());
            }

            return entries;
        }
    }

    /// <summary>
    /// Finds the main-document response facts for the post-render WAF hook.
    /// </summary>
    /// <param name="finalUrl">The final page URL.</param>
    /// <returns>Status and headers of the document response, when observed.</returns>
    public (int StatusCode, IReadOnlyDictionary<string, string> Headers)? GetDocumentResponse(string finalUrl)
    {
        lock (_gate)
        {
            EntryState? fallback = null;
            foreach (var state in _order)
            {
                if (state.ResourceType != "document" || state.Response is null)
                {
                    continue;
                }

                if (string.Equals(state.Url, finalUrl, StringComparison.Ordinal))
                {
                    return (state.Response.Status, state.Response.Headers);
                }

                fallback ??= state;
            }

            return fallback is null ? null : (fallback.Response!.Status, fallback.Response.Headers);
        }
    }

    private void OnRequestWillBeSent(JsonElement parameters)
    {
        var requestId = GetString(parameters, "requestId");
        if (requestId is null)
        {
            return;
        }

        var request = parameters.TryGetProperty("request", out var r) ? r : default;
        var url = GetString(request, "url") ?? string.Empty;
        var method = GetString(request, "method") ?? "GET";
        var type = GetString(parameters, "type");

        // A redirect hop finalizes the previous entry under the same request id.
        if (_byRequestId.TryGetValue(requestId, out var existing)
            && parameters.TryGetProperty("redirectResponse", out var redirectResponse))
        {
            ApplyResponse(existing, redirectResponse);
            existing.Completed = true;
            _byRequestId.Remove(requestId);
        }

        if (_order.Count >= _maxEntries)
        {
            return; // Soft bound: keep updating known entries, stop admitting new ones.
        }

        var state = new EntryState
        {
            Url = url,
            Method = method,
            ResourceType = MapResourceType(type),
            StartedMs = _clock.ElapsedMilliseconds,
        };
        _byRequestId[requestId] = state;
        _order.Add(state);
        TouchActivity(state);
    }

    private void OnResponseReceived(JsonElement parameters)
    {
        var requestId = GetString(parameters, "requestId");
        if (requestId is null || !_byRequestId.TryGetValue(requestId, out var state))
        {
            return;
        }

        if (parameters.TryGetProperty("response", out var response))
        {
            ApplyResponse(state, response);
        }

        if (parameters.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String)
        {
            state.ResourceType = MapResourceType(typeElement.GetString());
        }

        TouchActivity(state);
    }

    private void OnDataReceived(JsonElement parameters)
    {
        var requestId = GetString(parameters, "requestId");
        if (requestId is null || !_byRequestId.TryGetValue(requestId, out var state))
        {
            return;
        }

        if (parameters.TryGetProperty("encodedDataLength", out var encoded) && encoded.TryGetInt64(out var encodedLength))
        {
            state.EncodedBytes += encodedLength;
        }

        TouchActivity(state);
    }

    private void OnLoadingFinished(JsonElement parameters)
    {
        var requestId = GetString(parameters, "requestId");
        if (requestId is null || !_byRequestId.TryGetValue(requestId, out var state))
        {
            return;
        }

        if (parameters.TryGetProperty("encodedDataLength", out var encoded) && encoded.TryGetInt64(out var encodedLength))
        {
            // loadingFinished carries the authoritative total for the request.
            state.EncodedBytes = Math.Max(state.EncodedBytes, encodedLength);
            state.FinishedEncodedBytes = encodedLength;
        }

        state.Completed = true;
        TouchActivity(state);
    }

    private void OnLoadingFailed(JsonElement parameters)
    {
        var requestId = GetString(parameters, "requestId");
        if (requestId is null || !_byRequestId.TryGetValue(requestId, out var state))
        {
            return;
        }

        state.Completed = true;
        state.Failed = true;
        TouchActivity(state);
    }

    private void OnRequestServedFromCache(JsonElement parameters)
    {
        var requestId = GetString(parameters, "requestId");
        if (requestId is null || !_byRequestId.TryGetValue(requestId, out var state))
        {
            return;
        }

        state.FromCache = true;
        TouchActivity(state);
    }

    private static void ApplyResponse(EntryState state, JsonElement response)
    {
        var status = response.TryGetProperty("status", out var s) && s.TryGetInt32(out var code) ? code : 0;
        var mimeType = GetString(response, "mimeType");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (response.TryGetProperty("headers", out var headerElement) && headerElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var header in headerElement.EnumerateObject())
            {
                headers[header.Name] = header.Value.GetString() ?? string.Empty;
            }
        }

        string? headersText = GetString(response, "headersText");
        string? timingJson = response.TryGetProperty("timing", out var timing) && timing.ValueKind == JsonValueKind.Object
            ? timing.GetRawText()
            : null;

        var fromCache = GetBool(response, "fromDiskCache") || GetBool(response, "fromPrefetchCache");
        var fromServiceWorker = GetBool(response, "fromServiceWorker");
        long encoded = response.TryGetProperty("encodedDataLength", out var e) && e.TryGetInt64(out var el) ? el : 0;

        state.Response = new ResponseFacts
        {
            Status = status,
            MimeType = mimeType,
            Headers = headers,
            HeadersTextLength = headersText?.Length ?? 0,
            TimingJson = timingJson,
            EncodedDataLength = encoded,
        };
        state.FromCache |= fromCache;
        state.FromServiceWorker |= fromServiceWorker;
        state.EncodedBytes = Math.Max(state.EncodedBytes, encoded);
    }

    private void TouchActivity(EntryState state)
    {
        if (!IsStreaming(state, _clock.ElapsedMilliseconds))
        {
            _lastActivityMs = _clock.ElapsedMilliseconds;
        }
    }

    private bool IsStreaming(EntryState state, long nowMs)
    {
        if (state.ResourceType is "websocket" or "eventsource")
        {
            return true;
        }

        if (state.Response is { } response)
        {
            if (response.MimeType is not null && StreamingMimeTypes.Contains(response.MimeType))
            {
                return true;
            }

            // An open chunked body without a declared length is a streaming
            // response (live data feeds, chunked long-poll replies).
            if (response.Headers.TryGetValue("Transfer-Encoding", out var transferEncoding)
                && transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase)
                && !response.Headers.ContainsKey("Content-Length"))
            {
                return true;
            }
        }

        // Long-polling: a request still awaiting any response past the bound.
        return state.Response is null && !state.Completed && nowMs - state.StartedMs > _streamingPendingThresholdMs;
    }

    private static string MapResourceType(string? cdpType) => cdpType?.ToLowerInvariant() switch
    {
        null or "" => "other",
        "xhr" => "xhr",
        "eventsource" => "eventsource",
        "websocket" => "websocket",
        "texttrack" => "texttrack",
        "signedexchange" => "signedexchange",
        "cspviolationreport" => "cspviolationreport",
        var other => other,
    };

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetBool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private sealed class EntryState
    {
        public string Url { get; set; } = string.Empty;
        public string Method { get; set; } = "GET";
        public string ResourceType { get; set; } = "other";
        public long StartedMs { get; set; }
        public bool Completed { get; set; }
        public bool Failed { get; set; }
        public bool FromCache { get; set; }
        public bool FromServiceWorker { get; set; }
        public long EncodedBytes { get; set; }
        public long? FinishedEncodedBytes { get; set; }
        public ResponseFacts? Response { get; set; }

        public RenderedNetworkEntry ToEntry()
        {
            var encoded = FinishedEncodedBytes ?? EncodedBytes;
            var transferred = FromCache || FromServiceWorker
                ? 0
                : encoded + (Response?.HeadersTextLength ?? 0);
            return new RenderedNetworkEntry(
                Url,
                ResourceType,
                Method,
                Response?.Status ?? 0,
                Response?.MimeType,
                transferred,
                encoded,
                Response?.TimingJson,
                FromCache,
                FromServiceWorker);
        }
    }

    private sealed class ResponseFacts
    {
        public int Status { get; set; }
        public string? MimeType { get; set; }
        public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public int HeadersTextLength { get; set; }
        public string? TimingJson { get; set; }
        public long EncodedDataLength { get; set; }
    }
}
