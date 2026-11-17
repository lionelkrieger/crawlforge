namespace Fixture.E8;

/// <summary>Bundled CIDR + agent profiles; DNS verification is consented, user-triggered, off by default.</summary>
public sealed class LocalBotClassifier
{
    public bool IsBot(string ip, IReadOnlyList<string> bundledCidrRanges) => bundledCidrRanges.Count > 0;

    public string[] VerifyViaDns(string ip, bool userConsented) =>
        userConsented ? System.Net.Dns.GetHostEntry(ip).Aliases : [];
}
