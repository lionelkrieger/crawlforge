using System.Net;
using System.Net.Sockets;

namespace CrawlForge.Crawler.Tests.Rendering;

/// <summary>
/// Scripted loopback TCP server for upstream-proxy handshake tests
/// (HTTP CONNECT, SOCKS4a, SOCKS5). Handles a bounded number of connections.
/// </summary>
public sealed class ScriptedTcpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly Func<TcpClient, CancellationToken, Task> _handler;
    private int _connections;

    /// <summary>Number of connections accepted.</summary>
    public int Connections => Volatile.Read(ref _connections);

    /// <summary>The loopback port the server listens on.</summary>
    public int Port { get; }

    private ScriptedTcpServer(TcpListener listener, int port, Func<TcpClient, CancellationToken, Task> handler)
    {
        _listener = listener;
        Port = port;
        _handler = handler;
        _loop = RunAsync();
    }

    /// <summary>Starts a scripted server on a free loopback port.</summary>
    /// <param name="handler">The per-connection script.</param>
    /// <returns>The running server.</returns>
    public static ScriptedTcpServer Start(Func<TcpClient, CancellationToken, Task> handler)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return new ScriptedTcpServer(listener, port, handler);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Expected during shutdown.
        }

        _cts.Dispose();
    }

    private async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                break;
            }

            Interlocked.Increment(ref _connections);
            try
            {
                await _handler(client, _cts.Token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // Script finished or client disconnected.
            }
            finally
            {
                client.Dispose();
            }
        }
    }
}

/// <summary>Byte-stream helpers for scripted handshake tests.</summary>
public static class ScriptIo
{
    /// <summary>Reads exactly count bytes.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="count">Byte count.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The bytes read.</returns>
    public static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), cancellationToken);
            if (read == 0)
            {
                throw new IOException("Closed mid-read.");
            }

            offset += read;
        }

        return buffer;
    }

    /// <summary>Reads one CRLF-terminated line.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The line without its terminator.</returns>
    public static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var builder = new System.Text.StringBuilder();
        var buffer = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken);
            if (read == 0)
            {
                return builder.ToString();
            }

            if (buffer[0] == (byte)'\n')
            {
                return builder.ToString().TrimEnd('\r');
            }

            builder.Append((char)buffer[0]);
        }
    }

    /// <summary>Relays bytes bidirectionally until either side closes.</summary>
    /// <param name="left">One side.</param>
    /// <param name="right">The other side.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task RelayAsync(Stream left, Stream right, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var a = SafeCopyAsync(left, right, linked.Token);
        var b = SafeCopyAsync(right, left, linked.Token);
        await Task.WhenAny(a, b);
        linked.Cancel();
        await Task.WhenAll(a, b);
    }

    private static async Task SafeCopyAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        try
        {
            await source.CopyToAsync(destination, 8192, cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // One direction ended.
        }
    }
}
