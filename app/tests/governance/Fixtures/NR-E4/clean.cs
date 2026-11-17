namespace Fixture.E4;

/// <summary>Verdicts derive only from the bundled catalogue + observed response.</summary>
public sealed class LocalWafVerdict
{
    public string Classify(string observedBody, IReadOnlyList<string> bundledMarkers) =>
        bundledMarkers.Any(marker => observedBody.Contains(marker, StringComparison.Ordinal))
            ? "waf-challenge"
            : "none";
}
