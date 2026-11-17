using System;
using Microsoft.Win32;

namespace Fixture.Nr01;

public static class MachineIdentityProbe
{
    public static string ReadMachineGuid()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        return key?.GetValue("MachineGuid")?.ToString() ?? string.Empty;
    }

    public static string MachineId { get; set; } = string.Empty;
}
