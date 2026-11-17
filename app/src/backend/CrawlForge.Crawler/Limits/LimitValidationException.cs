namespace CrawlForge.Crawler.Limits;

/// <summary>
/// Descriptive local error raised when a limit override violates the validation
/// invariants of FS-CRAWL-001 §4.3. Invalid overrides are rejected before an
/// audit starts; the message names the layer, the field, and the rule broken.
/// No remote service is ever consulted.
/// </summary>
public sealed class LimitValidationException : Exception
{
    /// <summary>Creates a validation error for one override field.</summary>
    /// <param name="layer">Override layer that carried the invalid value.</param>
    /// <param name="field">Name of the invalid field.</param>
    /// <param name="value">The rejected value.</param>
    /// <param name="rule">Human-readable description of the violated rule.</param>
    public LimitValidationException(string layer, string field, object? value, string rule)
        : base($"Invalid crawl limit override in {layer} layer: '{field}' = {value ?? "(null)"} — {rule}")
    {
        Layer = layer;
        Field = field;
        Value = value;
        Rule = rule;
    }

    /// <summary>Override layer that carried the invalid value.</summary>
    public string Layer { get; }

    /// <summary>Name of the invalid field.</summary>
    public string Field { get; }

    /// <summary>The rejected value.</summary>
    public object? Value { get; }

    /// <summary>Human-readable description of the violated rule.</summary>
    public string Rule { get; }
}
