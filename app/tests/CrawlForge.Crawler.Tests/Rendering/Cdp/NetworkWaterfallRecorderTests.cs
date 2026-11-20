using System.Text.Json;
using CrawlForge.Crawler.Rendering;
using Xunit;

namespace CrawlForge.Crawler.Tests.Rendering;

/// <summary>
/// Pure CDP-event → locked-waterfall reduction (CR-006/CR-031 component
/// proof): field mapping, transferred bytes, redirects, cache/service-worker
/// flags, and the streaming exclusion feeding the §5.6.2 quiet-network test.
/// </summary>
public sealed class NetworkWaterfallRecorderTests
{
    [Fact]
    public void DocumentAndImage_ProduceLockedEntries_WithTransferredBytes()
    {
        var recorder = new NetworkWaterfallRecorder();
        recorder.OnEvent(Event("Network.requestWillBeSent", """
            {"requestId":"1","type":"Document","request":{"url":"http://127.0.0.1:9/page","method":"GET"}}
            """));
        recorder.OnEvent(Event("Network.responseReceived", """
            {"requestId":"1","type":"Document","response":{"url":"http://127.0.0.1:9/page","status":200,"mimeType":"text/html",
             "headers":{"Content-Type":"text/html"},"headersText":"HTTP/1.1 200 OK\r\n\r\n",
             "timing":{"requestTime":1.0,"sendStart":1.1,"receiveHeadersEnd":1.5}}}
            """));
        recorder.OnEvent(Event("Network.loadingFinished", """
            {"requestId":"1","encodedDataLength":120}
            """));
        recorder.OnEvent(Event("Network.requestWillBeSent", """
            {"requestId":"2","type":"Image","request":{"url":"http://127.0.0.1:9/pixel.gif","method":"GET"}}
            """));
        recorder.OnEvent(Event("Network.responseReceived", """
            {"requestId":"2","type":"Image","response":{"url":"http://127.0.0.1:9/pixel.gif","status":200,"mimeType":"image/gif","headers":{}}}
            """));
        recorder.OnEvent(Event("Network.loadingFinished", """
            {"requestId":"2","encodedDataLength":43}
            """));

        var entries = recorder.GetEntries();
        Assert.Equal(2, entries.Count);

        var document = entries[0];
        Assert.Equal("http://127.0.0.1:9/page", document.Url);
        Assert.Equal("document", document.ResourceType);
        Assert.Equal("GET", document.Method);
        Assert.Equal(200, document.StatusCode);
        Assert.Equal("text/html", document.MimeType);
        Assert.Equal(120, document.EncodedDataLength);
        // Transferred = encoded body + observed response head text.
        Assert.Equal(120 + "HTTP/1.1 200 OK\r\n\r\n".Length, document.TransferredBytes);
        Assert.NotNull(document.TimingJson);
        Assert.Contains("receiveHeadersEnd", document.TimingJson);
        Assert.False(document.FromCache);
        Assert.False(document.FromServiceWorker);

        var image = entries[1];
        Assert.Equal("image", image.ResourceType);
        // CR-031: the rendered image payload is measurable.
        Assert.True(image.TransferredBytes >= 43);
        Assert.Equal(43, image.EncodedDataLength);
        Assert.Equal(0, recorder.InFlightNonStreamingCount);
    }

    [Fact]
    public void RedirectHop_ProducesTwoEntries()
    {
        var recorder = new NetworkWaterfallRecorder();
        recorder.OnEvent(Event("Network.requestWillBeSent", """
            {"requestId":"1","type":"Document","request":{"url":"http://127.0.0.1:9/old","method":"GET"}}
            """));
        recorder.OnEvent(Event("Network.requestWillBeSent", """
            {"requestId":"1","type":"Document","request":{"url":"http://127.0.0.1:9/new","method":"GET"},
             "redirectResponse":{"url":"http://127.0.0.1:9/old","status":301,"mimeType":"text/html","headers":{"Location":"/new"}}}
            """));
        recorder.OnEvent(Event("Network.responseReceived", """
            {"requestId":"1","type":"Document","response":{"url":"http://127.0.0.1:9/new","status":200,"mimeType":"text/html","headers":{}}}
            """));
        recorder.OnEvent(Event("Network.loadingFinished", """
            {"requestId":"1","encodedDataLength":50}
            """));

        var entries = recorder.GetEntries();
        Assert.Equal(2, entries.Count);
        Assert.Equal("http://127.0.0.1:9/old", entries[0].Url);
        Assert.Equal(301, entries[0].StatusCode);
        Assert.Equal("http://127.0.0.1:9/new", entries[1].Url);
        Assert.Equal(200, entries[1].StatusCode);
    }

    [Fact]
    public void CacheHit_TransferredBytesZero_AndFlagged()
    {
        var recorder = new NetworkWaterfallRecorder();
        recorder.OnEvent(Event("Network.requestWillBeSent", """
            {"requestId":"1","type":"Image","request":{"url":"http://127.0.0.1:9/a.png","method":"GET"}}
            """));
        recorder.OnEvent(Event("Network.requestServedFromCache", """
            {"requestId":"1"}
            """));
        recorder.OnEvent(Event("Network.responseReceived", """
            {"requestId":"1","type":"Image","response":{"url":"http://127.0.0.1:9/a.png","status":200,"mimeType":"image/png","headers":{},"fromDiskCache":true}}
            """));
        recorder.OnEvent(Event("Network.loadingFinished", """
            {"requestId":"1","encodedDataLength":0}
            """));

        var entry = Assert.Single(recorder.GetEntries());
        Assert.True(entry.FromCache);
        Assert.Equal(0, entry.TransferredBytes);
    }

    [Fact]
    public void ServiceWorkerResponse_IsFlagged_AndTransferredZero()
    {
        var recorder = new NetworkWaterfallRecorder();
        recorder.OnEvent(Event("Network.requestWillBeSent", """
            {"requestId":"1","type":"Fetch","request":{"url":"http://127.0.0.1:9/api","method":"GET"}}
            """));
        recorder.OnEvent(Event("Network.responseReceived", """
            {"requestId":"1","type":"Fetch","response":{"url":"http://127.0.0.1:9/api","status":200,"mimeType":"application/json","headers":{},"fromServiceWorker":true}}
            """));
        recorder.OnEvent(Event("Network.loadingFinished", """
            {"requestId":"1","encodedDataLength":10}
            """));

        var entry = Assert.Single(recorder.GetEntries());
        Assert.True(entry.FromServiceWorker);
        Assert.Equal(0, entry.TransferredBytes);
        Assert.Equal("fetch", entry.ResourceType);
    }

    [Fact]
    public void DataReceived_Accumulates_WhenLoadingFinishedAbsent()
    {
        var recorder = new NetworkWaterfallRecorder();
        recorder.OnEvent(Event("Network.requestWillBeSent", """
            {"requestId":"1","type":"XHR","request":{"url":"http://127.0.0.1:9/data","method":"GET"}}
            """));
        recorder.OnEvent(Event("Network.dataReceived", """
            {"requestId":"1","dataLength":100,"encodedDataLength":40}
            """));
        recorder.OnEvent(Event("Network.dataReceived", """
            {"requestId":"1","dataLength":100,"encodedDataLength":60}
            """));

        var entry = Assert.Single(recorder.GetEntries());
        Assert.Equal(100, entry.EncodedDataLength);
        Assert.Equal(100, entry.TransferredBytes);
        Assert.Equal("xhr", entry.ResourceType);
        Assert.Equal(1, recorder.InFlightNonStreamingCount);
    }

    [Fact]
    public void LoadingFailed_CompletesEntry_WithStatusZero()
    {
        var recorder = new NetworkWaterfallRecorder();
        recorder.OnEvent(Event("Network.requestWillBeSent", """
            {"requestId":"1","type":"Script","request":{"url":"http://127.0.0.1:9/missing.js","method":"GET"}}
            """));
        recorder.OnEvent(Event("Network.loadingFailed", """
            {"requestId":"1","errorText":"net::ERR_CONNECTION_REFUSED"}
            """));

        var entry = Assert.Single(recorder.GetEntries());
        Assert.Equal(0, entry.StatusCode);
        Assert.Equal(0, recorder.InFlightNonStreamingCount);
    }

    [Theory]
    [InlineData("WebSocket", "websocket")]
    [InlineData("EventSource", "eventsource")]
    public void StreamingTypes_AreExcludedFromQuietTest(string cdpType, string expectedLabel)
    {
        var recorder = new NetworkWaterfallRecorder();
        recorder.OnEvent(Event("Network.requestWillBeSent", $$$"""
            {"requestId":"1","type":"{{{cdpType}}}","request":{"url":"http://127.0.0.1:9/stream","method":"GET"}}
            """));

        var entry = Assert.Single(recorder.GetEntries());
        Assert.Equal(expectedLabel, entry.ResourceType);
        // Open streaming connection must NOT block quiescence (§5.6.2).
        Assert.Equal(0, recorder.InFlightNonStreamingCount);
    }

    [Fact]
    public void EventStreamMime_IsExcludedFromQuietTest()
    {
        var recorder = new NetworkWaterfallRecorder();
        recorder.OnEvent(Event("Network.requestWillBeSent", """
            {"requestId":"1","type":"XHR","request":{"url":"http://127.0.0.1:9/sse","method":"GET"}}
            """));
        recorder.OnEvent(Event("Network.responseReceived", """
            {"requestId":"1","type":"XHR","response":{"url":"http://127.0.0.1:9/sse","status":200,"mimeType":"text/event-stream","headers":{}}}
            """));

        Assert.Equal(0, recorder.InFlightNonStreamingCount);
    }

    [Fact]
    public void OpenChunkedBodyWithoutLength_IsExcludedFromQuietTest()
    {
        var recorder = new NetworkWaterfallRecorder();
        recorder.OnEvent(Event("Network.requestWillBeSent", """
            {"requestId":"1","type":"Fetch","request":{"url":"http://127.0.0.1:9/feed","method":"GET"}}
            """));
        recorder.OnEvent(Event("Network.responseReceived", """
            {"requestId":"1","type":"Fetch","response":{"url":"http://127.0.0.1:9/feed","status":200,"mimeType":"application/json","headers":{"Transfer-Encoding":"chunked"}}}
            """));

        Assert.Equal(0, recorder.InFlightNonStreamingCount);
    }

    [Fact]
    public async Task LongPendingRequest_BecomesStreaming_AfterThreshold()
    {
        var recorder = new NetworkWaterfallRecorder(streamingPendingThresholdMs: 60);
        recorder.OnEvent(Event("Network.requestWillBeSent", """
            {"requestId":"1","type":"XHR","request":{"url":"http://127.0.0.1:9/long-poll","method":"GET"}}
            """));

        Assert.Equal(1, recorder.InFlightNonStreamingCount);
        await Task.Delay(120);
        Assert.Equal(0, recorder.InFlightNonStreamingCount);
    }

    [Fact]
    public async Task StreamingActivity_DoesNotResetNetworkQuiet()
    {
        var recorder = new NetworkWaterfallRecorder();
        recorder.OnEvent(Event("Network.requestWillBeSent", """
            {"requestId":"1","type":"EventSource","request":{"url":"http://127.0.0.1:9/sse","method":"GET"}}
            """));
        await Task.Delay(80);
        var ageBefore = recorder.LastActivityAgeMs;
        recorder.OnEvent(Event("Network.dataReceived", """
            {"requestId":"1","dataLength":10,"encodedDataLength":10}
            """));
        Assert.True(recorder.LastActivityAgeMs >= ageBefore);
    }

    [Fact]
    public void NonStreamingActivity_ResetsNetworkQuiet()
    {
        var recorder = new NetworkWaterfallRecorder();
        recorder.OnEvent(Event("Network.requestWillBeSent", """
            {"requestId":"1","type":"Script","request":{"url":"http://127.0.0.1:9/app.js","method":"GET"}}
            """));
        Assert.True(recorder.LastActivityAgeMs < 1000);
    }

    [Fact]
    public void MissingType_MapsToOther_AndDocumentResponseIsFound()
    {
        var recorder = new NetworkWaterfallRecorder();
        recorder.OnEvent(Event("Network.requestWillBeSent", """
            {"requestId":"1","request":{"url":"http://127.0.0.1:9/page","method":"GET"}}
            """));
        recorder.OnEvent(Event("Network.responseReceived", """
            {"requestId":"1","response":{"url":"http://127.0.0.1:9/page","status":403,"mimeType":"text/html","headers":{"cf-mitigated":"challenge"}}}
            """));

        var entry = Assert.Single(recorder.GetEntries());
        Assert.Equal("other", entry.ResourceType);

        // The WAF hook reads the document response facts.
        recorder.OnEvent(Event("Network.requestWillBeSent", """
            {"requestId":"2","type":"Document","request":{"url":"http://127.0.0.1:9/page","method":"GET"}}
            """));
        recorder.OnEvent(Event("Network.responseReceived", """
            {"requestId":"2","type":"Document","response":{"url":"http://127.0.0.1:9/page","status":200,"mimeType":"text/html","headers":{"Server":"test"}}}
            """));
        var document = recorder.GetDocumentResponse("http://127.0.0.1:9/page");
        Assert.NotNull(document);
        Assert.Equal(200, document!.Value.StatusCode);
        Assert.Equal("test", document.Value.Headers["Server"]);
    }

    [Fact]
    public void EntrySoftCap_BoundsRetainedEntries()
    {
        var recorder = new NetworkWaterfallRecorder(maxEntries: 3);
        for (var i = 0; i < 10; i++)
        {
            recorder.OnEvent(Event("Network.requestWillBeSent", $$$"""
                {"requestId":"{{{i}}}","type":"Image","request":{"url":"http://127.0.0.1:9/img-{{{i}}}","method":"GET"}}
                """));
        }

        Assert.Equal(3, recorder.GetEntries().Count);
    }

    [Fact]
    public void NonNetworkEvents_AreIgnored()
    {
        var recorder = new NetworkWaterfallRecorder();
        recorder.OnEvent(Event("Page.loadEventFired", """
            {"timestamp":1.0}
            """));
        Assert.Empty(recorder.GetEntries());
    }

    private static CdpEvent Event(string method, string paramsJson) =>
        new(method, JsonDocument.Parse(paramsJson).RootElement.Clone(), "session-test");
}
