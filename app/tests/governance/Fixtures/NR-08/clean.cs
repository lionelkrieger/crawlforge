namespace Fixture.Nr08;

/// <summary>Unlimited Limits Resolver: local defaults only, never remote or entitlement input.</summary>
public sealed class UnlimitedLimitsResolver
{
    public int? TotalPageCeiling => null;

    public int? PerDomainCeiling => null;

    public int ResolveConcurrency(int localOverride) => localOverride > 0 ? localOverride : 8;
}
