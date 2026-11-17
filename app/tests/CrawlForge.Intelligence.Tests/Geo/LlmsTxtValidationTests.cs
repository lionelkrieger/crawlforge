using CrawlForge.Intelligence.Geo;
using Xunit;

namespace CrawlForge.Intelligence.Tests.Geo;

/// <summary>
/// llms.txt structural validation (§13.1 convention: H1 title, blockquote
/// summary, H2 sections with markdown link lists) — pure function tests.
/// </summary>
public sealed class LlmsTxtValidationTests
{
    private const string WellFormed = """
        # Example Site

        > Example Site documents its platform for humans and AI agents.

        ## Docs

        - [Getting started](https://example.test/docs/start)
        - [API reference](https://example.test/docs/api)

        ## Guides

        - [Deploy guide](https://example.test/guides/deploy)
        """;

    private static GeoReadinessAnalyzer CreateAnalyzer() =>
        new(new FakeProvider(), new FakeContentStore(), new Support.TestDomParser());

    [Fact]
    public void WellFormedDocument_PassesValidation()
    {
        var analyzer = CreateAnalyzer();
        var result = analyzer.ValidateLlmsTxt(WellFormed, "https://example.test/llms.txt");

        Assert.True(result.IsPresent);
        Assert.True(result.IsWellFormed);
        Assert.Equal("example.test", result.Host);
        Assert.Empty(result.BrokenLinks);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void MissingH1_ProducesIssue()
    {
        var analyzer = CreateAnalyzer();
        var body = "> Summary only\n\n## Docs\n\n- [A](https://example.test/a)\n";
        var result = analyzer.ValidateLlmsTxt(body, "https://example.test/llms.txt");

        Assert.False(result.IsWellFormed);
        Assert.Contains(result.Issues, i => i.StartsWith("missing-h1:", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingBlockquote_ProducesIssue()
    {
        var analyzer = CreateAnalyzer();
        var body = "# Site\n\n## Docs\n\n- [A](https://example.test/a)\n";
        var result = analyzer.ValidateLlmsTxt(body, "https://example.test/llms.txt");

        Assert.False(result.IsWellFormed);
        Assert.Contains(result.Issues, i => i.StartsWith("missing-blockquote:", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingSections_ProducesIssue()
    {
        var analyzer = CreateAnalyzer();
        var body = "# Site\n\n> Summary\n";
        var result = analyzer.ValidateLlmsTxt(body, "https://example.test/llms.txt");

        Assert.False(result.IsWellFormed);
        Assert.Contains(result.Issues, i => i.StartsWith("missing-sections:", StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyBody_IsNotPresent()
    {
        var analyzer = CreateAnalyzer();
        var result = analyzer.ValidateLlmsTxt("   ", "https://example.test/llms.txt");

        Assert.False(result.IsPresent);
        Assert.False(result.IsWellFormed);
    }

    [Fact]
    public void RelativeLinks_ResolveAgainstBase()
    {
        var document = LlmsTxtParser.Parse(
            "# Site\n\n> Summary\n\n## Docs\n\n- [A](/docs/a)\n",
            "https://example.test/llms.txt");

        var link = Assert.Single(document.Links);
        Assert.Equal("https://example.test/docs/a", link.ResolvedUrl);
    }

    [Fact]
    public void UnresolvableLink_WithoutBase_IsBroken()
    {
        var analyzer = CreateAnalyzer();
        var body = "# Site\n\n> Summary\n\n## Docs\n\n- [A](/docs/a)\n";
        var result = analyzer.ValidateLlmsTxt(body, baseUrl: null);

        Assert.Contains("/docs/a", result.BrokenLinks);
    }

    [Fact]
    public void Parser_ExtractsSectionsAndSummary()
    {
        var document = LlmsTxtParser.Parse(WellFormed, "https://example.test/llms.txt");

        Assert.Equal("Example Site", document.Title);
        Assert.StartsWith("Example Site documents", document.Summary, StringComparison.Ordinal);
        Assert.Equal(3, document.Links.Count);
        Assert.True(document.HasH2SectionsWithLinks);
        Assert.Equal(2, document.Sections.Count(s => s.Level == 2));
    }

    /// <summary>Minimal provider double for pure-function tests (never opened).</summary>
    private sealed class FakeProvider : Contracts.Persistence.IDbProvider
    {
        public Contracts.Enums.DatabaseProviderKind Kind => Contracts.Enums.DatabaseProviderKind.Sqlite;

        public string Dialect => "fake";

        public Task<System.Data.Common.DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Pure validation must not touch the database.");

        public Task<Contracts.Persistence.DbConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Pure validation must not touch the database.");
    }

    /// <summary>Minimal content-store double for pure-function tests (never opened).</summary>
    private sealed class FakeContentStore : Contracts.Persistence.IContentStore
    {
        public Task<Contracts.Entities.ContentArtifactDto> PutAsync(long auditId, long urlId, Contracts.Enums.ContentArtifactKind kind, Stream content, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Pure validation must not touch the content store.");

        public Task<Stream?> OpenReadAsync(string storageKey, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Pure validation must not touch the content store.");

        public Task<bool> DeleteAsync(string storageKey, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Pure validation must not touch the content store.");
    }
}
