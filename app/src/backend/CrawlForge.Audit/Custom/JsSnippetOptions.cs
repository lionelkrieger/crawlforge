namespace CrawlForge.Audit.Custom;

/// <summary>
/// Local configuration of the JavaScript snippet runner (FS-CRAWL-001 §7.17
/// safety bounds). Constructor-injected implementation choice — the locked
/// <c>JsSnippetDefinition</c> contract carries key/name/source/MaxRows; these
/// options carry the execution timeout, path matching, and the local script
/// library (exclusion E1: snippets and loaded scripts are local user artifacts
/// only — never fetched).
/// </summary>
public sealed record JsSnippetOptions
{
    /// <summary>Default per-snippet execution timeout; a snippet exceeding it is aborted and isolated.</summary>
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Per-snippet timeout overrides keyed by snippet key.</summary>
    public IReadOnlyDictionary<string, TimeSpan> TimeoutOverrides { get; init; } =
        new Dictionary<string, TimeSpan>();

    /// <summary>Per-snippet URL-path scoping keyed by snippet key; absent snippets run on all paths.</summary>
    public IReadOnlyDictionary<string, PathScope> PathScopes { get; init; } =
        new Dictionary<string, PathScope>();

    /// <summary>
    /// Local script library exposed through the wrapper's <c>cf.loadScript(name)</c>
    /// API. Names map to local user-authored source; unknown names report an
    /// error. No remote loading path exists.
    /// </summary>
    public IReadOnlyDictionary<string, string> LocalScriptLibrary { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Hard ceiling on rows per snippet, clamping any larger definition MaxRows.</summary>
    public int MaxRowsCeiling { get; init; } = 1000;

    /// <summary>Maximum statements a snippet may execute (loop bound alongside the timeout).</summary>
    public int MaxStatements { get; init; } = 1_000_000;

    /// <summary>Validates the option values.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A bound is out of range.</exception>
    public void Validate()
    {
        if (DefaultTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(DefaultTimeout), DefaultTimeout, "The default timeout must be positive.");
        }

        if (MaxRowsCeiling <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRowsCeiling), MaxRowsCeiling, "The row ceiling must be positive.");
        }

        if (MaxStatements <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxStatements), MaxStatements, "The statement bound must be positive.");
        }
    }
}