using System.Net;
using System.Net.Sockets;

namespace CrawlForge.Host.Hosting;

/// <summary>
/// Port strategy per process-model spec §2.2: attempt the configured default loopback
/// port first; if occupied, fall back to an OS-assigned ephemeral port (0, resolved by
/// Kestrel at bind time). The backend never binds anything but the loopback interface.
/// </summary>
public static class LoopbackPortResolver
{
    public static int ResolvePort(int configuredPort)
    {
        if (configuredPort <= 0)
        {
            return 0;
        }

        var probe = new TcpListener(IPAddress.Loopback, configuredPort);
        try
        {
            probe.Start();
            return configuredPort;
        }
        catch (SocketException)
        {
            // Occupied: fall back to an OS-assigned ephemeral port.
            return 0;
        }
        finally
        {
            probe.Stop();
        }
    }
}
