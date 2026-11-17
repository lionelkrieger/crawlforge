namespace Fixture.Nr02;

/// <summary>User-configured export destination credentials (the user's own, not vendor).</summary>
public sealed record DestinationCredentials(string UserName, string Password);

public sealed class LocalSettings
{
    public bool FeatureEnabled { get; set; } = true;
}
