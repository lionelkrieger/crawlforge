using CrawlForge.Contracts.Crawl;
using CrawlForge.Crawler.Politeness;
using CrawlForge.Crawler.Tests.Limits;
using Xunit;

namespace CrawlForge.Crawler.Tests.Politeness;

/// <summary>
/// Per-audit cookie jar (FS-CRAWL-001 §5.11): the cookie-gate fixture trap
/// (200 after one redirect with the jar on; redirect loop cookieless),
/// browser-style host/path scoping, security-attribute capture for the D2
/// rules, user-cookie merge, response-change recording, thread safety.
/// </summary>
public class AuditCookieJarTests
{
    private static readonly Uri GateUrl = new("http://127.0.0.1:47314/cookie-gate/");

    /// <summary>
    /// Fixture trap cookie-gate, jar on (default): the 302 sets the gate
    /// cookie, the next hop carries it, and the page returns 200 after one
    /// redirect — exactly the fetcher's hop-by-hop redirect flow.
    /// </summary>
    [Fact]
    public async Task CookieGateReturns200AfterOneRedirectWithJarOn()
    {
        var gate = new CookieGateHandler();
        var jar = new AuditCookieJar();
        using var client = new HttpClient(new CookieJarMessageHandler(jar, gate));

        var first = await client.GetAsync(GateUrl);
        Assert.Equal(System.Net.HttpStatusCode.Redirect, first.StatusCode);

        var location = new Uri(GateUrl, first.Headers.Location!);
        var second = await client.GetAsync(location);
        Assert.Equal(System.Net.HttpStatusCode.OK, second.StatusCode);
        Assert.Contains("Cookie gate passed", await second.Content.ReadAsStringAsync());

        Assert.Equal(2, gate.RequestCount);
        Assert.Null(gate.ObservedCookies[0]);
        Assert.Contains("fixture_gate=1", gate.ObservedCookies[1]);
    }

    /// <summary>
    /// Fixture trap cookie-gate, cookieless: no cookie is retained, every hop
    /// redirects again — the loop the diagnostic reports.
    /// </summary>
    [Fact]
    public async Task CookielessCrawlLoopsOnTheGate()
    {
        var gate = new CookieGateHandler();
        var jar = new AuditCookieJar(new CookieJarOptions { Enabled = false });
        using var client = new HttpClient(new CookieJarMessageHandler(jar, gate));

        var hops = new List<RedirectHop>();
        var current = GateUrl;
        for (var i = 0; i < 5; i++)
        {
            var response = await client.GetAsync(current);
            if (response.StatusCode != System.Net.HttpStatusCode.Redirect)
            {
                break;
            }

            var target = new Uri(current, response.Headers.Location!);
            hops.Add(new RedirectHop(i, current.AbsoluteUri, target.AbsoluteUri, (int)response.StatusCode));
            current = target;
        }

        Assert.Equal(5, hops.Count);
        Assert.All(gate.ObservedCookies, cookie => Assert.Null(cookie));

        var finding = CookielessRedirectLoopDiagnostic.Create(
            1, GateUrl.AbsoluteUri, hops, ["fixture_gate"], cookieJarEnabled: false);
        Assert.NotNull(finding);
        Assert.Equal("crawl.cookieless-redirect-loop", finding!.RuleKey);
        Assert.Contains("fixture_gate", finding.EvidenceJson);
    }

    /// <summary>Host-only cookies match the exact host, not subdomains.</summary>
    [Fact]
    public void HostOnlyCookiesDoNotLeakToSubdomains()
    {
        var jar = new AuditCookieJar();
        jar.SetFromResponse(new Uri("https://example.com/"), ["a=1; Path=/"]);
        Assert.Equal("a=1", jar.GetCookieHeader(new Uri("https://example.com/page")));
        Assert.Null(jar.GetCookieHeader(new Uri("https://sub.example.com/page")));
        Assert.Null(jar.GetCookieHeader(new Uri("https://other.com/")));
    }

    /// <summary>Domain cookies domain-match parent and subdomains; unrelated domains are rejected.</summary>
    [Fact]
    public void DomainCookiesScopeByDomainMatch()
    {
        var jar = new AuditCookieJar();
        jar.SetFromResponse(new Uri("https://www.example.com/"), ["a=1; Domain=example.com; Path=/"]);
        Assert.Equal("a=1", jar.GetCookieHeader(new Uri("https://example.com/")));
        Assert.Equal("a=1", jar.GetCookieHeader(new Uri("https://shop.example.com/")));
        Assert.Null(jar.GetCookieHeader(new Uri("https://notexample.com/")));

        // A cookie scoped to an unrelated domain is rejected outright.
        jar.SetFromResponse(new Uri("https://example.com/"), ["evil=1; Domain=other.org; Path=/"]);
        Assert.Null(jar.GetCookieHeader(new Uri("https://other.org/")));
        Assert.DoesNotContain(jar.GetCookies(), c => c.Name == "evil");
    }

    /// <summary>Path scoping follows RFC 6265: default path is the request directory.</summary>
    [Fact]
    public void PathScopingFollowsRfc6265()
    {
        var jar = new AuditCookieJar();
        jar.SetFromResponse(new Uri("https://example.com/dir/page.html"), ["a=1"]);
        Assert.Equal("a=1", jar.GetCookieHeader(new Uri("https://example.com/dir/other")));
        Assert.Equal("a=1", jar.GetCookieHeader(new Uri("https://example.com/dir")));
        Assert.Null(jar.GetCookieHeader(new Uri("https://example.com/")));
        Assert.Null(jar.GetCookieHeader(new Uri("https://example.com/dir2/x")));

        jar.SetFromResponse(new Uri("https://example.com/"), ["root=1; Path=/"]);
        Assert.Contains("root=1", jar.GetCookieHeader(new Uri("https://example.com/anything")));
    }

    /// <summary>Same-name cookies are all sent, longer path first (RFC 6265 §5.4); the map surface is first-wins.</summary>
    [Fact]
    public void LongerPathsAreSentFirst()
    {
        var jar = new AuditCookieJar();
        jar.SetFromResponse(new Uri("https://example.com/"), ["a=root; Path=/"]);
        jar.SetFromResponse(new Uri("https://example.com/dir/"), ["a=dir; Path=/dir"]);
        Assert.Equal("a=dir; a=root", jar.GetCookieHeader(new Uri("https://example.com/dir/page")));
        Assert.Equal("a=root", jar.GetCookieHeader(new Uri("https://example.com/other")));
        Assert.Equal("dir", jar.GetCookieMap(new Uri("https://example.com/dir/page"))["a"]);
    }

    /// <summary>Secure cookies are never sent over plain HTTP.</summary>
    [Fact]
    public void SecureCookiesRequireHttps()
    {
        var jar = new AuditCookieJar();
        jar.SetFromResponse(new Uri("https://example.com/"), ["s=1; Secure; Path=/"]);
        Assert.Equal("s=1", jar.GetCookieHeader(new Uri("https://example.com/page")));
        Assert.Null(jar.GetCookieHeader(new Uri("http://example.com/page")));
    }

    /// <summary>Expiry and Max-Age are honored; Max-Age wins; expired cookies delete.</summary>
    [Fact]
    public void ExpiryAndMaxAgeAreHonored()
    {
        var time = new ManualTimeProvider();
        var jar = new AuditCookieJar(timeProvider: time);
        jar.SetFromResponse(new Uri("https://example.com/"), ["a=1; Max-Age=60; Path=/"]);
        Assert.Equal("a=1", jar.GetCookieHeader(new Uri("https://example.com/")));

        time.Advance(TimeSpan.FromSeconds(61));
        Assert.Null(jar.GetCookieHeader(new Uri("https://example.com/")));

        // Max-Age wins over a conflicting Expires.
        jar.SetFromResponse(new Uri("https://example.com/"),
            ["b=1; Max-Age=3600; Expires=Wed, 01 Jan 2020 00:00:00 GMT; Path=/"]);
        Assert.Equal("b=1", jar.GetCookieHeader(new Uri("https://example.com/")));

        // Max-Age=0 deletes the stored cookie.
        jar.SetFromResponse(new Uri("https://example.com/"), ["b=1; Max-Age=0; Path=/"]);
        Assert.Null(jar.GetCookieHeader(new Uri("https://example.com/")));
    }

    /// <summary>Security attributes are captured for the D2 rules (data, not findings).</summary>
    [Fact]
    public void SecurityAttributesAreCaptured()
    {
        var jar = new AuditCookieJar();
        jar.SetFromResponse(new Uri("https://example.com/login"),
            ["sess=abc; Path=/; Secure; HttpOnly; SameSite=Lax", "track=1; Path=/; SameSite=None"]);
        var observations = jar.SecurityObservations;
        Assert.Equal(2, observations.Count);

        var sess = observations.Single(o => o.Name == "sess");
        Assert.True(sess.Secure);
        Assert.True(sess.HttpOnly);
        Assert.Equal("lax", sess.SameSite);
        Assert.True(sess.IsSession);
        Assert.Equal("https://example.com/login", sess.SourceUrl);

        var track = observations.Single(o => o.Name == "track");
        Assert.False(track.Secure);
        Assert.False(track.HttpOnly);
        Assert.Equal("none", track.SameSite);
    }

    /// <summary>Cookie prefixes are hardened: __Secure- needs Secure, __Host- needs Secure+host-only+Path=/.</summary>
    [Fact]
    public void CookiePrefixesAreHardened()
    {
        var jar = new AuditCookieJar();
        jar.SetFromResponse(new Uri("https://example.com/"),
        [
            "__Secure-a=1; Path=/",               // rejected: no Secure
            "__Secure-b=1; Secure; Path=/",       // accepted
            "__Host-c=1; Secure; Path=/",         // accepted
            "__Host-d=1; Secure; Path=/; Domain=example.com" // rejected: domain scope
        ]);
        var names = jar.GetCookies().Select(c => c.Name).ToList();
        Assert.Equal(["__Host-c", "__Secure-b"], names);
    }

    /// <summary>User-supplied cookies merge in and are sent even in cookieless mode.</summary>
    [Fact]
    public void UserCookiesMergeAndSurviveCookielessMode()
    {
        var jar = new AuditCookieJar(new CookieJarOptions { Enabled = false });
        jar.MergeCookies(new Uri("https://example.com/"), new Dictionary<string, string> { ["auth"] = "token" });
        Assert.Equal("auth=token", jar.GetCookieHeader(new Uri("https://example.com/page")));

        // Site cookies are still neither stored nor replayed.
        jar.SetFromResponse(new Uri("https://example.com/"), ["site=1; Path=/"]);
        Assert.Equal("auth=token", jar.GetCookieHeader(new Uri("https://example.com/page")));
        Assert.Empty(jar.SecurityObservations);
    }

    /// <summary>The read/merge surface for cf-wp-09-03 enumerates and maps cookies.</summary>
    [Fact]
    public void ReadSurfaceEnumeratesAndMapsCookies()
    {
        var jar = new AuditCookieJar();
        jar.SetFromResponse(new Uri("https://example.com/"), ["a=1; Path=/", "b=2; Path=/dir"]);
        jar.MergeCookies(new Uri("https://example.com/"), new Dictionary<string, string> { ["c"] = "3" }, CookieSource.Authenticator);

        var all = jar.GetCookies();
        Assert.Equal(3, all.Count);
        Assert.Contains(all, c => c.Name == "c" && c.Source == CookieSource.Authenticator);

        var map = jar.GetCookieMap(new Uri("https://example.com/dir/page"));
        Assert.Equal("1", map["a"]);
        Assert.Equal("2", map["b"]);
        Assert.Equal("3", map["c"]);

        Assert.Throws<ArgumentException>(() =>
            jar.MergeCookies(new Uri("https://example.com/"), new Dictionary<string, string> { ["x"] = "1" }, CookieSource.Site));
    }

    /// <summary>Response changes after cookie set are recorded (§5.11).</summary>
    [Fact]
    public void ResponseChangesAfterCookieSetAreRecorded()
    {
        var jar = new AuditCookieJar();
        const string url = "https://example.com/page";
        jar.NoteResponse(url, "hash-before");
        jar.SetFromResponse(new Uri(url), ["gate=1; Path=/"]);
        jar.NoteResponse(url, "hash-after");

        var changes = jar.ResponseChanges;
        Assert.Single(changes);
        Assert.Equal("hash-before", changes[0].PreviousContentHash);
        Assert.Equal("hash-after", changes[0].NewContentHash);
        Assert.Equal(["gate"], changes[0].CookieNamesSetBetween);

        // No cookie set between observations: no change recorded.
        jar.NoteResponse("https://example.com/other", "h1");
        jar.NoteResponse("https://example.com/other", "h2");
        Assert.Single(jar.ResponseChanges);
    }

    /// <summary>Concurrent workers share one jar safely (pipeline MaxConcurrency=2).</summary>
    [Fact]
    public void ConcurrentAccessIsThreadSafe()
    {
        var jar = new AuditCookieJar();
        var origin = new Uri("https://example.com/");
        Parallel.For(0, 200, i =>
        {
            if (i % 2 == 0)
            {
                jar.SetFromResponse(origin, [$"c{i}=v; Path=/"]);
            }
            else
            {
                jar.GetCookieHeader(new Uri($"https://example.com/page-{i}"));
                jar.GetCookies();
            }
        });

        Assert.Equal(100, jar.GetCookies().Count);
        Assert.Equal(100, jar.SecurityObservations.Count);
    }

    /// <summary>Clear drops all state (audit teardown).</summary>
    [Fact]
    public void ClearDropsAllState()
    {
        var jar = new AuditCookieJar();
        jar.SetFromResponse(new Uri("https://example.com/"), ["a=1; Path=/"]);
        jar.NoteResponse("https://example.com/", "h1");
        jar.Clear();
        Assert.Empty(jar.GetCookies());
        Assert.Empty(jar.SecurityObservations);
        Assert.Empty(jar.ResponseChanges);
        Assert.Null(jar.GetCookieHeader(new Uri("https://example.com/")));
    }
}
