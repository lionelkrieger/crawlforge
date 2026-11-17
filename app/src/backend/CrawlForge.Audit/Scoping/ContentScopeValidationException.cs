namespace CrawlForge.Audit.Scoping;

/// <summary>
/// Raised when a content scope carries invalid selectors at configuration time
/// (FS-CRAWL-002 §4.2: selector syntax is validated at configuration time;
/// invalid selectors are rejected with a descriptive local error).
/// </summary>
public sealed class ContentScopeValidationException : Exception
{
    /// <summary>Creates the exception with per-entry error descriptions.</summary>
    /// <param name="entryErrors">One descriptive error per invalid entry.</param>
    public ContentScopeValidationException(IReadOnlyList<string> entryErrors)
        : base("Invalid content scope: " + string.Join("; ", entryErrors))
    {
        EntryErrors = entryErrors;
    }

    /// <summary>Per-entry validation errors, in list order.</summary>
    public IReadOnlyList<string> EntryErrors { get; }
}
