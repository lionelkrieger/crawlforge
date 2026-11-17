using System.Net.Http.Json;

namespace CrawlForge.Crawler.Scheduling;

/// <summary>
/// Posts completion messages to notification endpoints the user configured
/// themselves (FS-CRAWL-001 §6.5). The endpoint always comes from the user's
/// pre-authorized schedule configuration; this client never contacts any other
/// destination. The <see cref="HttpClient"/> is supplied by the host (tests
/// substitute a fake handler — no network is touched).
/// </summary>
public sealed class HttpNotificationEndpointClient : INotificationEndpointClient
{
    private readonly HttpClient _httpClient;

    /// <summary>Creates the client over the host-supplied HTTP client.</summary>
    /// <param name="httpClient">HTTP client used for the user's own endpoint.</param>
    public HttpNotificationEndpointClient(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    /// <inheritdoc/>
    public async Task SendAsync(string endpoint, ScheduleNotificationPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentNullException.ThrowIfNull(payload);

        using var response = await _httpClient.PostAsJsonAsync(endpoint, payload, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}
