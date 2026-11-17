using CrawlForge.Audit.Custom;
using CrawlForge.Audit.Tests.Custom.TestSupport;
using CrawlForge.Contracts.Audit;
using CrawlForge.Contracts.Enums;
using Xunit;

namespace CrawlForge.Audit.Tests.Custom;

/// <summary>
/// Custom content search (FS-CRAWL-001 §7.16, criterion #30): positive and
/// negative terms, regex, scoped text, raw markup, bounds, isolation,
/// offline determinism.
/// </summary>
public sealed class CustomContentSearchTests
{
    [Fact]
    public async Task PlainTerm_CountsMatches_AndBuildsSnippets()
    {
        DomDocument document = CustomDomBuilder.Document(
            CustomContextFactory.PageUrl,
            CustomDomBuilder.Page("Search", new[] { CustomDomBuilder.Text("p", "Alpha beta alpha gamma alpha.") }));
        var search = new CustomContentSearch();
        var items = new[] { new CustomSearchItem("alpha", "alpha") };

        IReadOnlyList<ContentSearchMatch> matches = await search.SearchAsync(CustomContextFactory.Context(document: document), items);

        ContentSearchMatch match = Assert.Single(matches);
        Assert.Equal("alpha", match.ItemKey);
        Assert.Equal(3, match.MatchCount);
        Assert.Equal(3, match.Snippets.Count);
        Assert.Contains("alpha", match.Snippets[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PlainTerm_IsCaseInsensitive()
    {
        DomDocument document = CustomDomBuilder.Document(
            CustomContextFactory.PageUrl,
            CustomDomBuilder.Page("Case", new[] { CustomDomBuilder.Text("p", "ALPHA alpha Alpha") }));
        var search = new CustomContentSearch();

        IReadOnlyList<ContentSearchMatch> matches = await search.SearchAsync(
            CustomContextFactory.Context(document: document), new[] { new CustomSearchItem("a", "alpha") });

        Assert.Equal(3, Assert.Single(matches).MatchCount);
    }

    [Fact]
    public async Task RegexItem_CountsMatches()
    {
        DomDocument document = CustomDomBuilder.Document(
            CustomContextFactory.PageUrl,
            CustomDomBuilder.Page("Regex", new[] { CustomDomBuilder.Text("p", "id1 id22 id333") }));
        var search = new CustomContentSearch();
        var items = new[] { new CustomSearchItem("ids", "id\\d+", IsRegex: true) };

        IReadOnlyList<ContentSearchMatch> matches = await search.SearchAsync(CustomContextFactory.Context(document: document), items);

        Assert.Equal(3, Assert.Single(matches).MatchCount);
    }

    [Fact]
    public async Task NegativeTerm_Present_SuppressesAllMatches()
    {
        DomDocument document = CustomDomBuilder.Document(
            CustomContextFactory.PageUrl,
            CustomDomBuilder.Page("Negative", new[] { CustomDomBuilder.Text("p", "cheap product, discontinued") }));
        var search = new CustomContentSearch(new CustomSearchOptions
        {
            NegativeTerms = new Dictionary<string, IReadOnlyList<string>> { ["price"] = new[] { "discontinued" } },
        });
        var items = new[] { new CustomSearchItem("price", "cheap") };

        IReadOnlyList<ContentSearchMatch> matches = await search.SearchAsync(CustomContextFactory.Context(document: document), items);

        Assert.Equal(0, Assert.Single(matches).MatchCount);
        Assert.Empty(matches[0].Snippets);
    }

    [Fact]
    public async Task NegativeTerm_Absent_AllowsMatches()
    {
        DomDocument document = CustomDomBuilder.Document(
            CustomContextFactory.PageUrl,
            CustomDomBuilder.Page("Negative", new[] { CustomDomBuilder.Text("p", "cheap product, in stock") }));
        var search = new CustomContentSearch(new CustomSearchOptions
        {
            NegativeTerms = new Dictionary<string, IReadOnlyList<string>> { ["price"] = new[] { "discontinued" } },
        });

        IReadOnlyList<ContentSearchMatch> matches = await search.SearchAsync(
            CustomContextFactory.Context(document: document), new[] { new CustomSearchItem("price", "cheap") });

        Assert.Equal(1, Assert.Single(matches).MatchCount);
    }

    [Fact]
    public async Task ScopedText_UsedWhenConfigured()
    {
        DomDocument document = CustomDomBuilder.Document(
            CustomContextFactory.PageUrl,
            CustomDomBuilder.Page("Scope", new[] { CustomDomBuilder.Text("p", "boilerplate needle outside scope") }));
        var scoped = new ScopedVisibleText("content area without the term", 5, Array.Empty<string>());
        var search = new CustomContentSearch();

        IReadOnlyList<ContentSearchMatch> matches = await search.SearchAsync(
            CustomContextFactory.Context(document: document, scopedText: scoped), new[] { new CustomSearchItem("n", "needle") });

        Assert.Equal(0, Assert.Single(matches).MatchCount);
    }

    [Fact]
    public async Task RawMarkupTarget_MatchesTags()
    {
        DomDocument document = CustomDomBuilder.Document(
            CustomContextFactory.PageUrl,
            CustomDomBuilder.Page("Markup", new[] { CustomDomBuilder.Div("tracked", CustomDomBuilder.Text("p", "text")) }));
        var search = new CustomContentSearch(new CustomSearchOptions
        {
            RawMarkupTargets = new Dictionary<string, bool> { ["cls"] = true },
        });

        IReadOnlyList<ContentSearchMatch> matches = await search.SearchAsync(
            CustomContextFactory.Context(document: document), new[] { new CustomSearchItem("cls", "class=\"tracked\"") });

        Assert.Equal(1, Assert.Single(matches).MatchCount);
    }

    [Fact]
    public async Task MatchesPerPageCap_ClampsCount_AndReportsDiscard()
    {
        DomDocument document = CustomDomBuilder.Document(
            CustomContextFactory.PageUrl,
            CustomDomBuilder.Page("Many", new[] { CustomDomBuilder.Text("p", string.Join(" ", Enumerable.Repeat("x", 10))) }));
        var search = new CustomContentSearch(new CustomSearchOptions { MaxMatchesPerPage = 4, MaxSnippetsPerPage = 2 });

        CustomSearchResult result = await search.SearchWithDiagnosticsAsync(
            CustomContextFactory.Context(document: document), new[] { new CustomSearchItem("x", "x") });

        ContentSearchMatch match = Assert.Single(result.Matches);
        Assert.Equal(4, match.MatchCount);
        Assert.Equal(2, match.Snippets.Count);
        Assert.Contains(result.Diagnostics, d => d.Kind == CustomDiagnosticKinds.Discarded && d.Count == 6);
    }

    [Fact]
    public async Task CatastrophicRegex_TimesOut_Isolated()
    {
        DomDocument document = CustomDomBuilder.Document(
            CustomContextFactory.PageUrl,
            CustomDomBuilder.Page("Evil", new[] { CustomDomBuilder.Text("p", new string('a', 40) + "!") }));
        var search = new CustomContentSearch(new CustomSearchOptions { RegexTimeout = TimeSpan.FromMilliseconds(200) });
        var items = new[]
        {
            new CustomSearchItem("evil", "(a+)+$", IsRegex: true),
            new CustomSearchItem("ok", "aaa"),
        };

        CustomSearchResult result = await search.SearchWithDiagnosticsAsync(CustomContextFactory.Context(document: document), items);

        Assert.Contains(result.Diagnostics, d => d.ItemKey == "evil" && d.Kind == CustomDiagnosticKinds.Timeout);
        Assert.Contains(result.Matches, m => m.ItemKey == "ok" && m.MatchCount > 0);
    }

    [Fact]
    public async Task InvalidRegex_Isolated_OtherItemsRun()
    {
        DomDocument document = CustomDomBuilder.Document(
            CustomContextFactory.PageUrl,
            CustomDomBuilder.Page("Bad", new[] { CustomDomBuilder.Text("p", "fine") }));
        var search = new CustomContentSearch();
        var items = new[]
        {
            new CustomSearchItem("bad", "[unclosed", IsRegex: true),
            new CustomSearchItem("ok", "fine"),
        };

        CustomSearchResult result = await search.SearchWithDiagnosticsAsync(CustomContextFactory.Context(document: document), items);

        Assert.Contains(result.Diagnostics, d => d.ItemKey == "bad" && d.Kind == CustomDiagnosticKinds.Error);
        Assert.Contains(result.Matches, m => m.ItemKey == "ok" && m.MatchCount == 1);
    }

    [Fact]
    public async Task PathScope_ExcludesPage()
    {
        DomDocument document = CustomDomBuilder.Document(
            CustomContextFactory.PageUrl,
            CustomDomBuilder.Page("Scope", new[] { CustomDomBuilder.Text("p", "term") }));
        var search = new CustomContentSearch(new CustomSearchOptions
        {
            PathScopes = new Dictionary<string, PathScope> { ["scoped"] = new PathScope(new[] { "/blog" }, Array.Empty<string>()) },
        });

        CustomSearchResult result = await search.SearchWithDiagnosticsAsync(
            CustomContextFactory.Context(document: document), new[] { new CustomSearchItem("scoped", "term") });

        Assert.Equal(0, Assert.Single(result.Matches).MatchCount);
        Assert.Contains(result.Diagnostics, d => d.Kind == CustomDiagnosticKinds.OutOfScope);
    }

    [Fact]
    public async Task Rerun_IsDeterministic()
    {
        DomDocument document = CustomDomBuilder.Document(
            CustomContextFactory.PageUrl,
            CustomDomBuilder.Page("Determinism", new[] { CustomDomBuilder.Text("p", "one two one two one") }));
        PageAuditContext context = CustomContextFactory.Context(document: document);
        var search = new CustomContentSearch();
        var items = new[] { new CustomSearchItem("one", "one"), new CustomSearchItem("nums", "\\d+", IsRegex: true) };

        CustomSearchResult first = await search.SearchWithDiagnosticsAsync(context, items);
        CustomSearchResult second = await search.SearchWithDiagnosticsAsync(context, items);

        Assert.Equal(first.Matches.Count, second.Matches.Count);
        for (int i = 0; i < first.Matches.Count; i++)
        {
            Assert.Equal(first.Matches[i].ItemKey, second.Matches[i].ItemKey);
            Assert.Equal(first.Matches[i].MatchCount, second.Matches[i].MatchCount);
            Assert.Equal(first.Matches[i].Snippets, second.Matches[i].Snippets);
        }
    }

    [Fact]
    public async Task NullArguments_Throw()
    {
        var search = new CustomContentSearch();
        await Assert.ThrowsAsync<ArgumentNullException>(() => search.SearchAsync(null!, Array.Empty<CustomSearchItem>()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => search.SearchAsync(CustomContextFactory.Context(), null!));
    }
}