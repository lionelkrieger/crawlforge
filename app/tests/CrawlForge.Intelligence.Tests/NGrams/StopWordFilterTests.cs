using CrawlForge.Intelligence.NGrams;
using Xunit;

namespace CrawlForge.Intelligence.Tests.NGrams;

/// <summary>Stop-word filter tests (FS-CRAWL-002 §5.2): bundled offline lists,
/// language resolution, and local user_stopwords additions over real SQLite.</summary>
public sealed class StopWordFilterTests
{
    /// <summary>Bundled English stop words are removed; order is preserved.</summary>
    [Fact]
    public void RemoveStopWords_Filters_Bundled_English_Preserving_Order()
    {
        var filter = new StopWordFilter();

        var result = filter.RemoveStopWords(["the", "quick", "brown", "fox", "and", "the", "dog"], "en");

        Assert.Equal(["quick", "brown", "fox", "dog"], result);
    }

    /// <summary>Language variants resolve to the base bundled list (en-GB → en).</summary>
    [Fact]
    public void RemoveStopWords_Resolves_Language_Variants()
    {
        var filter = new StopWordFilter();

        var result = filter.RemoveStopWords(["the", "cat"], "en-GB");

        Assert.Equal(["cat"], result);
    }

    /// <summary>Unknown languages fall back to the preferred list (§7.8 rule).</summary>
    [Fact]
    public void RemoveStopWords_Unknown_Language_Falls_Back_To_Preferred()
    {
        var filter = new StopWordFilter(preferredLanguage: "en");

        var result = filter.RemoveStopWords(["the", "cat"], "zz");

        Assert.Equal(["cat"], result);
    }

    /// <summary>Empty page language falls back to the preferred language.</summary>
    [Fact]
    public void RemoveStopWords_Empty_Language_Uses_Preferred()
    {
        var filter = new StopWordFilter(preferredLanguage: "en");

        var result = filter.RemoveStopWords(["the", "cat"], string.Empty);

        Assert.Equal(["cat"], result);
    }

    /// <summary>Filtering is case-insensitive.</summary>
    [Fact]
    public void RemoveStopWords_Is_Case_Insensitive()
    {
        var filter = new StopWordFilter();

        var result = filter.RemoveStopWords(["THE", "Cat"], "en");

        Assert.Equal(["Cat"], result);
    }

    /// <summary>User stop words from the local table apply alongside the bundled list.</summary>
    [Fact]
    public async Task RemoveStopWords_Applies_User_Stopwords_From_Database()
    {
        using var stack = await NGramTestStack.CreateAsync();
        await stack.SeedUserStopwordAsync("en", "fox");
        var filter = new StopWordFilter(stack.Provider);

        var result = filter.RemoveStopWords(["the", "quick", "fox"], "en");

        Assert.Equal(["quick"], result);
    }

    /// <summary>User additions for the base language apply to variants; refresh picks up late additions.</summary>
    [Fact]
    public async Task RemoveStopWords_User_Additions_Support_Variants_And_Refresh()
    {
        using var stack = await NGramTestStack.CreateAsync();
        var filter = new StopWordFilter(stack.Provider);
        Assert.Equal(["quick", "fox"], filter.RemoveStopWords(["quick", "fox"], "en-GB"));

        await stack.SeedUserStopwordAsync("en", "fox");
        filter.RefreshUserStopwords();

        Assert.Equal(["quick"], filter.RemoveStopWords(["quick", "fox"], "en-GB"));
    }

    /// <summary>Spanish bundled list filters Spanish stop words.</summary>
    [Fact]
    public void RemoveStopWords_Filters_Spanish_Bundled_List()
    {
        var filter = new StopWordFilter();

        var result = filter.RemoveStopWords(["el", "gato", "negro"], "es");

        Assert.Equal(["gato", "negro"], result);
    }
}
