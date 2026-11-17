namespace Fixture.Nr03;

/// <summary>
/// Regression (wave-0C gate): genuine OS identity reads must STILL fire NR-03 after the
/// SpecialFolder path-enum exemption.
/// </summary>
public static class OsIdentityRead
{
    public static string CurrentUser() => Environment.UserName;

    public static string CurrentDomain() => Environment.UserDomainName;
}
