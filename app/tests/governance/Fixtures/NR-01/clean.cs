using System.Security.Cryptography;

namespace Fixture.Nr01;

public static class LocalEntropy
{
    // Entropy secrets are app-generated random bytes only (clean-room guardrail #1).
    // No registry, WMI, or hardware identifier is ever read here.
    public static byte[] CreateSecret(int bytes) => RandomNumberGenerator.GetBytes(bytes);

    public static string CreateLocalToken() => Guid.NewGuid().ToString("N");
}
