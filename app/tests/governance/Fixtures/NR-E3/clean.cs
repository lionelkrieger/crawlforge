namespace Fixture.E3;

/// <summary>Detection and reporting only — never solving or bypass.</summary>
public sealed class WafChallengeDetector
{
    public bool IsChallengePage(string body) => body.Contains("cf-chl", StringComparison.Ordinal);

    public string ClassifyProvider(string body) =>
        body.Contains("datadome", StringComparison.Ordinal) ? "DataDome" : "Unknown";
}
