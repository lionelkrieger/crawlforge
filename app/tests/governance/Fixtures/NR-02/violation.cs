namespace Fixture.Nr02;

public sealed class LicenseClient
{
    public string LicenseKey { get; set; } = string.Empty;

    public string EntitlementState { get; private set; } = "trial";

    public void ActivateLicense(string activationKey)
    {
        // Periodic recheck against the vendor license server.
        var headers = new Dictionary<string, string> { ["X-License-Key"] = LicenseKey };
    }
}
