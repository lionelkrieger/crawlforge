namespace Fixture.Nr01;

/// <summary>
/// Regression (wave-0C gate): genuine machine/OS identity reads must STILL fire NR-01
/// after the SpecialFolder path-enum exemption.
/// </summary>
public static class OsMachineIdentity
{
    public static string Machine() => Environment.MachineName;

    public static string Os() => Environment.OSVersion.ToString();
}
