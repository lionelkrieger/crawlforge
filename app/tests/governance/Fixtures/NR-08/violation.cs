namespace Fixture.Nr08;

public sealed class EntitlementLimits
{
    public int LicenseLimit { get; set; }

    public int ResolvePageCeiling(string planTier) => planTier == "pro" ? 10000 : 100;

    public bool CheckEntitlement(string feature) => false;
}
