namespace CrawlForge.Crawler.Tests.Fetching.TestSupport;

/// <summary>
/// Fake transport: scripted responses/exceptions per send, recording every
/// request. No network is touched.
/// </summary>
internal sealed class ScriptedHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    public List<HttpRequestMessage> Requests { get; } = new();

    public int DelayMilliseconds { get; set; }

    public ScriptedHttpMessageHandler Enqueue(HttpResponseMessage response)
    {
        _responses.Enqueue(_ => response);
        return this;
    }

    public ScriptedHttpMessageHandler Enqueue(Func<HttpRequestMessage, HttpResponseMessage> factory)
    {
        _responses.Enqueue(factory);
        return this;
    }

    public ScriptedHttpMessageHandler EnqueueException(Exception exception)
    {
        _responses.Enqueue(_ => throw exception);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (DelayMilliseconds > 0)
        {
            await Task.Delay(DelayMilliseconds, cancellationToken).ConfigureAwait(false);
        }

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("No scripted response remaining for request " + request.RequestUri);
        }

        return _responses.Dequeue()(request);
    }

    public static HttpResponseMessage Response(
        int statusCode,
        string? content = null,
        string? contentType = null,
        string? location = null)
    {
        var response = new HttpResponseMessage((System.Net.HttpStatusCode)statusCode);
        if (content is not null)
        {
            response.Content = new StringContent(content, System.Text.Encoding.UTF8, contentType ?? "text/html");
        }

        if (location is not null)
        {
            response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        }

        return response;
    }
}