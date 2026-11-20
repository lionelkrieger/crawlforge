namespace CrawlForge.Audit.StructuredData;

/// <summary>
/// One entry of the bundled retired/restricted rich-result list (FS-CRAWL-001
/// §7.13 as amended by CR-030, D-021): a search-gallery feature type whose
/// rich-result display was retired or restricted by the search gallery.
/// </summary>
/// <param name="Type">Canonical local type name (schema.org vocabulary).</param>
/// <param name="Status">"retired" (feature removed from the gallery) or
/// "restricted" (feature shown only for a narrow site class).</param>
/// <param name="Since">Effective date of the gallery change (ISO date).</param>
/// <param name="Note">Human-readable guidance emitted with the finding.</param>
public sealed record DeprecatedRichResultEntry(
    string Type,
    string Status,
    string Since,
    string Note)
{
    /// <summary>Status token: the feature was removed from the search gallery.</summary>
    public const string StatusRetired = "retired";

    /// <summary>Status token: the feature is shown only for a narrow site class.</summary>
    public const string StatusRestricted = "restricted";

    /// <summary>Whether the feature was fully retired (never rich-result eligible).</summary>
    public bool IsRetired => string.Equals(Status, StatusRetired, StringComparison.Ordinal);
}

/// <summary>
/// Bundled, VERSIONED list of retired/restricted rich-result types (FS-CRAWL-001
/// §7.13, CR-030/D-021): deprecated terms and retired rich-result types are
/// flagged from this list. The list is embedded in the product and updates only
/// with product updates — it is NEVER fetched at runtime (exclusions E2/E4).
/// </summary>
/// <remarks>
/// <para>
/// Provenance (clean-room, publicly documented search-gallery changes):
/// HowTo rich results were retired from Google Search (desktop and mobile) in
/// September 2023; FAQ rich results were restricted to prominent, authoritative
/// government and health websites in August 2023. HowTo is one entry of this
/// list, as the change record names it.
/// </para>
/// <para>
/// Semantics: a retired type is still parsed and syntax/semantics-validated
/// against the bundled vocabulary, but it is never rich-result eligible. A
/// restricted type keeps its vocabulary-driven eligibility judgment (site
/// authority is not observable offline); the restriction is reported as a
/// <c>structured-data.deprecated-term</c> Warning so the owner can decide
/// whether the markup is worth keeping. Type terms resolve by local name
/// exactly like the vocabulary (<see cref="StructuredDataVocabulary.LocalName"/>),
/// so URL-qualified and prefixed itemtypes match.
/// </para>
/// </remarks>
public static class DeprecatedRichResultList
{
    /// <summary>
    /// Version of the bundled list (updates only with product updates; emitted
    /// with every deprecated-term finding so re-audits are attributable).
    /// </summary>
    public const string Version = "2026.11";

    /// <summary>The bundled entries in stable order.</summary>
    public static IReadOnlyList<DeprecatedRichResultEntry> Entries { get; } =
    [
        new("HowTo", DeprecatedRichResultEntry.StatusRetired, "2023-09-13",
            "HowTo rich results were retired from Google Search (desktop and mobile). " +
            "The markup is still parsed and validated, but the type is no longer rich-result eligible."),
        new("FAQPage", DeprecatedRichResultEntry.StatusRestricted, "2023-08-08",
            "FAQ rich results are shown only for prominent, authoritative government and health websites. " +
            "Other sites' FAQPage markup is validated but rarely displayed."),
    ];

    /// <summary>
    /// Resolves the list entry for a type name. Namespace-qualified names
    /// (URLs or prefixes) resolve by local segment, exactly like the bundled
    /// vocabulary.
    /// </summary>
    /// <param name="typeName">Raw type name from @type / itemtype / typeof.</param>
    /// <returns>The entry, or null when the type is not on the list.</returns>
    public static DeprecatedRichResultEntry? Find(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return null;
        }

        var local = StructuredDataVocabulary.LocalName(typeName);
        foreach (var entry in Entries)
        {
            if (string.Equals(entry.Type, local, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>Whether the type is a fully retired rich-result feature.</summary>
    /// <param name="typeName">Raw type name.</param>
    /// <returns>True when the type is on the list with status "retired".</returns>
    public static bool IsRetired(string? typeName) => Find(typeName)?.IsRetired == true;
}
