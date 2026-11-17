using System.Net;
using CrawlForge.Contracts.Api;
using CrawlForge.Intelligence.LogAnalyzer;

namespace CrawlForge.Intelligence.Tests.LogAnalyzer.Support;

/// <summary>
/// Recording hub double: captures published events for assertions (no network).
/// Also samples managed memory at each publication so scale tests can prove
/// bounded memory DURING streaming, not only after it.
/// </summary>
public sealed class RecordingHub : IRealtimeHub
{
    /// <summary>Published events in order.</summary>
    public List<HubEvent> Events { get; } = [];

    /// <summary>Managed memory (bytes) sampled at each publication.</summary>
    public List<long> ManagedMemorySamples { get; } = [];

    /// <inheritdoc />
    public Task PublishAsync(HubEvent hubEvent, CancellationToken cancellationToken = default)
    {
        Events.Add(hubEvent);
        ManagedMemorySamples.Add(GC.GetTotalMemory(forceFullCollection: false));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Scripted DNS resolver double — keeps the verification suites fully offline.
/// </summary>
public sealed class FakeDnsResolver : IDnsResolver
{
    /// <summary>Reverse map: IP → hostname.</summary>
    public Dictionary<string, string> Reverse { get; } = new(StringComparer.Ordinal);

    /// <summary>Forward map: hostname → addresses.</summary>
    public Dictionary<string, string[]> Forward { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Number of reverse lookups performed.</summary>
    public int ReverseCalls { get; private set; }

    /// <summary>Number of forward lookups performed.</summary>
    public int ForwardCalls { get; private set; }

    /// <inheritdoc />
    public Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken = default)
    {
        ForwardCalls++;
        return Task.FromResult(Forward.TryGetValue(host, out var addresses)
            ? addresses.Select(IPAddress.Parse).ToArray()
            : []);
    }

    /// <inheritdoc />
    public Task<string?> GetHostNameAsync(IPAddress address, CancellationToken cancellationToken = default)
    {
        ReverseCalls++;
        return Task.FromResult(Reverse.TryGetValue(address.ToString(), out var host) ? host : null);
    }
}

/// <summary>
/// Sample log content for each supported format (synthetic, local-only).
/// </summary>
public static class LogSamples
{
    /// <summary>Googlebot desktop user-agent.</summary>
    public const string GooglebotDesktopUa = "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)";

    /// <summary>Googlebot smartphone user-agent.</summary>
    public const string GooglebotSmartphoneUa = "Mozilla/5.0 (Linux; Android 6.0.1; Nexus 5X Build/MMB29P) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)";

    /// <summary>Bingbot user-agent.</summary>
    public const string BingbotUa = "Mozilla/5.0 (compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm)";

    /// <summary>GPTBot user-agent.</summary>
    public const string GptBotUa = "Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko); compatible; GPTBot/1.0; +https://openai.com/gptbot";

    /// <summary>A plain browser user-agent.</summary>
    public const string HumanUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    /// <summary>Builds one Apache/Nginx Combined line.</summary>
    /// <param name="ip">Client IP.</param>
    /// <param name="timestamp">Timestamp text (dd/MMM/yyyy:HH:mm:ss zzz).</param>
    /// <param name="method">HTTP method.</param>
    /// <param name="path">Request path.</param>
    /// <param name="status">Status code.</param>
    /// <param name="bytes">Response bytes ("-" for none).</param>
    /// <param name="userAgent">User-agent.</param>
    /// <returns>The combined-format line.</returns>
    public static string CombinedLine(string ip, string timestamp, string method, string path, int status, string bytes, string userAgent) =>
        $"{ip} - - [{timestamp}] \"{method} {path} HTTP/1.1\" {status} {bytes} \"-\" \"{userAgent}\"";

    /// <summary>A small combined log with mixed bots, humans, and malformed lines.</summary>
    /// <returns>The sample content.</returns>
    public static string CombinedSample() => string.Join('\n',
        CombinedLine("66.249.66.1", "10/Oct/2026:13:55:36 +0000", "GET", "/page-a", 200, "1024", GooglebotDesktopUa),
        CombinedLine("66.249.66.2", "10/Oct/2026:13:56:00 +0000", "GET", "/page-b", 200, "2048", GooglebotSmartphoneUa),
        CombinedLine("157.55.39.10", "10/Oct/2026:13:57:00 +0000", "GET", "/page-a", 200, "1024", BingbotUa),
        CombinedLine("203.0.113.5", "10/Oct/2026:13:58:00 +0000", "GET", "/page-c", 404, "512", HumanUa),
        "this line is malformed",
        CombinedLine("66.249.66.1", "10/Oct/2026:14:00:00 +0000", "GET", "/orphan-page", 200, "4096", GooglebotDesktopUa));

    /// <summary>A small IIS W3C log with header-driven fields.</summary>
    /// <returns>The sample content.</returns>
    public static string W3cSample() => string.Join('\n',
        "#Software: Microsoft Internet Information Services 10.0",
        "#Version: 1.0",
        "#Date: 2026-10-10 13:55:00",
        "#Fields: date time c-ip cs-method cs-uri-stem cs-uri-query sc-status sc-bytes cs(User-Agent)",
        $"2026-10-10 13:55:36 66.249.66.1 GET /page-a - 200 1024 {GooglebotDesktopUa.Replace(' ', '+')}",
        "2026-10-10 13:56:00 157.55.39.10 GET /page-b session=abc 200 2048 bingbot/2.0",
        "2026-10-10 13:57:00 203.0.113.5 GET /page-c - 404 512 Mozilla/5.0",
        "malformed w3c line");

    /// <summary>A small Cloudflare JSONL export.</summary>
    /// <returns>The sample content.</returns>
    public static string CloudflareJsonSample() => string.Join('\n',
        $$"""{"ClientIP":"66.249.66.1","ClientRequestMethod":"GET","ClientRequestURI":"/page-a","EdgeResponseStatus":200,"EdgeResponseBytes":1024,"EdgeStartTimestamp":"2026-10-10T13:55:36Z","ClientRequestUserAgent":"{{GooglebotDesktopUa}}"}""",
        $$"""{"ClientIP":"20.15.240.5","ClientRequestMethod":"GET","ClientRequestURI":"/page-b","EdgeResponseStatus":200,"EdgeResponseBytes":2048,"EdgeStartTimestamp":"2026-10-10T13:56:00Z","ClientRequestUserAgent":"{{GptBotUa}}"}""",
        "{malformed json");

    /// <summary>A small Cloudflare CSV export.</summary>
    /// <returns>The sample content.</returns>
    public static string CloudflareCsvSample() => string.Join('\n',
        "ClientIP,ClientRequestMethod,ClientRequestURI,EdgeResponseStatus,EdgeResponseBytes,EdgeStartTimestamp,ClientRequestUserAgent",
        $"66.249.66.1,GET,/page-a,200,1024,2026-10-10T13:55:36Z,\"{GooglebotDesktopUa}\"",
        "157.55.39.10,GET,/page-b,200,2048,2026-10-10T13:56:00Z,bingbot/2.0");
}
