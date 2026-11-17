using System.Net;
using System.Net.Sockets;

namespace Fixture.Net01;

public static class LoopbackListener
{
    public static void Open(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        listener.Stop();
    }
}
