using System.Text;
using CrawlForge.Contracts.Crawl;
using CrawlForge.Crawler.ListMode;
using Xunit;

namespace CrawlForge.Crawler.Tests.ListMode;

/// <summary>
/// User-configured site authentication (FS-CRAWL-001 §5.1 custom headers /
/// cookies; §5.6 credential scoping): basic Authorization and form-login
/// cookies attach ONLY to in-scope origins; jar cookies merge with correct
/// precedence; no credentials configured reproduces baseline exactly.
/// </summary>
public class CrawlRequestAuthenticatorTests
{
    private static HttpFetchRequest Request(
        string url = "https://target.example/page",
        IReadOnlyDictionary<string, string>? headers = null,
        IReadOnlyDictionary<string, string>? cookies = null) =>
        new(url, "GET", headers, cookies, TimeoutSeconds: 30, FollowRedirects: false);

    private static CrawlAuthenticationSettings BasicAndCookies => new()
    {
        BasicUsername = "user",
        BasicPassword = "pässword",
        UserCookies = new Dictionary<string, string> { ["session"] = "user-session", ["pref"] = "dark" },
    };

    [Fact]
    public void BasicHeader_InScope_IsRfc7617()
    {
        var authenticator = new CrawlRequestAuthenticator(new CrawlAuthenticationSettings
        {
            BasicUsername = "user",
            BasicPassword = "pässword",
        });

        var decorated = authenticator.Decorate(Request(), inScope: true);

        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("user:pässword"));
        Assert.Equal(expected, decorated.Headers!["Authorization"]);
        Assert.Equal(expected, CrawlRequestAuthenticator.BuildBasicHeaderValue("user", "pässword"));
    }

    [Fact]
    public void Credentials_OutOfScope_NeverAttach()
    {
        var authenticator = new CrawlRequestAuthenticator(BasicAndCookies);
        var request = Request(url: "https://third-party.example/analytics");

        var decorated = authenticator.Decorate(request, inScope: false);

        Assert.Same(request, decorated);
        Assert.Null(decorated.Headers);
        Assert.Null(decorated.Cookies);
    }

    [Fact]
    public void Credentials_OutOfScope_WithJarCookies_NeverAttach()
    {
        var authenticator = new CrawlRequestAuthenticator(BasicAndCookies);
        var jar = new Dictionary<string, string> { ["session"] = "jar-session" };

        var decorated = authenticator.Decorate(Request(url: "https://cdn.example/asset"), inScope: false, jar);

        Assert.Null(decorated.Headers);
        Assert.Null(decorated.Cookies);
    }

    [Fact]
    public void UserCookies_InScope_AreAttached()
    {
        var authenticator = new CrawlRequestAuthenticator(BasicAndCookies);

        var decorated = authenticator.Decorate(Request(), inScope: true);

        Assert.Equal("user-session", decorated.Cookies!["session"]);
        Assert.Equal("dark", decorated.Cookies["pref"]);
        Assert.NotNull(decorated.Headers!["Authorization"]);
    }

    [Fact]
    public void CookieMerge_JarOverridesUser_RequestOverridesJar()
    {
        var authenticator = new CrawlRequestAuthenticator(new CrawlAuthenticationSettings
        {
            UserCookies = new Dictionary<string, string> { ["session"] = "user", ["only-user"] = "u" },
        });
        var jar = new Dictionary<string, string> { ["session"] = "jar", ["only-jar"] = "j" };
        var request = Request(cookies: new Dictionary<string, string> { ["session"] = "request", ["only-request"] = "r" });

        var decorated = authenticator.Decorate(request, inScope: true, jar);

        Assert.Equal("request", decorated.Cookies!["session"]);
        Assert.Equal("u", decorated.Cookies["only-user"]);
        Assert.Equal("j", decorated.Cookies["only-jar"]);
        Assert.Equal("r", decorated.Cookies["only-request"]);
    }

    [Fact]
    public void NoCredentials_PassesThroughUnchanged()
    {
        var authenticator = new CrawlRequestAuthenticator();
        var request = Request(headers: new Dictionary<string, string> { ["X-Trace"] = "1" });

        var decorated = authenticator.Decorate(request, inScope: true);

        Assert.Same(request, decorated);
    }

    [Fact]
    public void ExistingAuthorization_IsNotOverwritten()
    {
        var authenticator = new CrawlRequestAuthenticator(new CrawlAuthenticationSettings
        {
            BasicUsername = "user",
            BasicPassword = "pass",
        });
        var request = Request(headers: new Dictionary<string, string> { ["Authorization"] = "Bearer user-token" });

        var decorated = authenticator.Decorate(request, inScope: true);

        Assert.Equal("Bearer user-token", decorated.Headers!["Authorization"]);
    }

    [Fact]
    public void NonCredentialHeaders_PassThroughForEveryOrigin()
    {
        var authenticator = new CrawlRequestAuthenticator(BasicAndCookies);
        var request = Request(headers: new Dictionary<string, string> { ["User-Agent"] = "CrawlForge" });

        var inScope = authenticator.Decorate(request, inScope: true);
        var outOfScope = authenticator.Decorate(request, inScope: false);

        Assert.Equal("CrawlForge", inScope.Headers!["User-Agent"]);
        Assert.Equal("CrawlForge", outOfScope.Headers!["User-Agent"]);
    }

    [Fact]
    public void UsernameWithColon_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new CrawlRequestAuthenticator(new CrawlAuthenticationSettings
        {
            BasicUsername = "bad:user",
            BasicPassword = "pass",
        }));
    }

    [Fact]
    public void PartialBasicCredentials_DoNotAttach()
    {
        var authenticator = new CrawlRequestAuthenticator(new CrawlAuthenticationSettings { BasicUsername = "user" });

        var decorated = authenticator.Decorate(Request(), inScope: true);

        Assert.Null(decorated.Headers);
    }

    [Fact]
    public void CookieOnlySettings_AttachCookiesWithoutAuthorization()
    {
        var authenticator = new CrawlRequestAuthenticator(new CrawlAuthenticationSettings
        {
            UserCookies = new Dictionary<string, string> { ["session"] = "form-login" },
        });

        var decorated = authenticator.Decorate(Request(), inScope: true);

        Assert.Equal("form-login", decorated.Cookies!["session"]);
        Assert.False(decorated.Headers!.ContainsKey("Authorization"));
    }
}
