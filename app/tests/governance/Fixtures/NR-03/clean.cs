namespace Fixture.Nr03;

/// <summary>Local automation token (MCP) — locally generated, no accounts.</summary>
public sealed record LocalAutomationToken(string Token, DateTimeOffset CreatedUtc);

public static class TokenFactory
{
    public static LocalAutomationToken Create() =>
        new(Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)), DateTimeOffset.UtcNow);
}
