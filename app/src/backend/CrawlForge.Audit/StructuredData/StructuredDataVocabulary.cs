namespace CrawlForge.Audit.StructuredData;

/// <summary>
/// Bundled open vocabulary for structured-data validation (FS-CRAWL-001 §7.13):
/// a curated, offline subset of schema.org type definitions plus the property
/// sets required for search-gallery rich-result eligibility. The vocabulary is
/// embedded (no runtime fetch, exclusion E2/E4); it ships with the product and
/// updates only with product updates.
/// </summary>
/// <remarks>
/// Provenance: property names follow the public schema.org vocabulary
/// (https://schema.org); required sets follow the documented search-gallery
/// feature requirements for each supported type. Types not listed here are
/// parsed and syntax-checked but carry no required-property obligations.
/// </remarks>
public static class StructuredDataVocabulary
{
    /// <summary>One vocabulary entry: required and recommended properties of a type.</summary>
    /// <param name="Required">Properties that must be present and non-empty.</param>
    /// <param name="Recommended">Properties that should be present (advisory).</param>
    /// <param name="RichResultFeature">Whether the type is a supported rich-result feature.</param>
    public sealed record TypeRule(
        IReadOnlyList<string> Required,
        IReadOnlyList<string> Recommended,
        bool RichResultFeature);

    private static readonly IReadOnlyDictionary<string, TypeRule> Rules =
        new Dictionary<string, TypeRule>(StringComparer.OrdinalIgnoreCase)
        {
            ["Article"] = new(["headline", "image", "datePublished"], ["author", "dateModified", "publisher"], true),
            ["NewsArticle"] = new(["headline", "image", "datePublished"], ["author", "dateModified", "publisher"], true),
            ["BlogPosting"] = new(["headline", "image", "datePublished"], ["author", "dateModified", "publisher"], true),
            ["Product"] = new(["name", "image", "offers"], ["description", "sku", "brand", "aggregateRating"], true),
            ["Recipe"] = new(["name", "image", "recipeIngredient", "recipeInstructions"], ["author", "cookTime", "nutrition", "aggregateRating"], true),
            ["FAQPage"] = new(["mainEntity"], [], true),
            ["HowTo"] = new(["name", "step"], ["image", "totalTime", "supply", "tool"], true),
            ["BreadcrumbList"] = new(["itemListElement"], [], true),
            ["Event"] = new(["name", "startDate", "location"], ["endDate", "offers", "performer", "image"], true),
            ["LocalBusiness"] = new(["name", "address"], ["image", "telephone", "openingHours", "geo"], true),
            ["Review"] = new(["itemReviewed", "reviewRating", "author"], ["datePublished", "reviewBody"], true),
            ["VideoObject"] = new(["name", "thumbnailUrl", "uploadDate"], ["description", "duration", "contentUrl"], true),
            ["JobPosting"] = new(["title", "description", "datePosted", "hiringOrganization"], ["validThrough", "employmentType", "jobLocation"], true),
            ["Course"] = new(["name", "description", "provider"], ["courseCode", "hasCourseInstance"], true),
            ["WebSite"] = new(["name", "url"], ["potentialAction", "publisher"], true),
            ["Organization"] = new(["name", "url", "logo"], ["sameAs", "contactPoint"], true),
            ["Person"] = new(["name"], ["url", "image", "jobTitle"], false),
            ["WebPage"] = new(["name"], ["description", "url"], false),
            ["CreativeWork"] = new(["name"], ["author", "datePublished"], false),
            ["Thing"] = new(["name"], ["description", "url"], false),
        };

    /// <summary>
    /// Resolves the vocabulary rule for a type name. Namespace-qualified names
    /// (URLs or prefixes) are reduced to their local segment; common schema.org
    /// aliases resolve to their canonical entry.
    /// </summary>
    /// <param name="typeName">Raw type name from @type or itemtype.</param>
    /// <returns>The rule, or null when the type is outside the bundled subset.</returns>
    public static TypeRule? Resolve(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return null;
        }

        var local = LocalName(typeName);
        return Rules.TryGetValue(local, out var rule) ? rule : null;
    }

    /// <summary>Whether the type is a supported rich-result feature.</summary>
    /// <param name="typeName">Raw type name.</param>
    /// <returns>True when the type participates in rich-result eligibility.</returns>
    public static bool IsRichResultFeature(string? typeName) => Resolve(typeName)?.RichResultFeature == true;

    /// <summary>Reduces a possibly namespace-qualified type name to its local segment.</summary>
    /// <param name="typeName">Raw type name (e.g. "https://schema.org/Article").</param>
    /// <returns>The local type name.</returns>
    public static string LocalName(string typeName)
    {
        var trimmed = typeName.Trim();
        var separator = trimmed.LastIndexOfAny(['/', ':', '#']);
        return separator >= 0 && separator < trimmed.Length - 1 ? trimmed[(separator + 1)..] : trimmed;
    }
}
