using System.Net;
using System.Net.Sockets;

namespace Fixture.Net01;

public static class WildcardListener
{
    public static void Open(int port)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
    }

    public static IPEndPoint CreateWildcardEndpoint(int port) => new IPEndPoint(IPAddress.IPv6Any, port);
}
