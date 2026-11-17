using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace CrawlForge.Crawler.Proxy;

/// <summary>
/// Default outbound connector (§5.9): direct TCP to user-configured crawl
/// targets, or a tunnel chained through the user-configured upstream proxy
/// (HTTP-connect class or SOCKS class) with per-host bypass and optional
/// direct fallback. Opens no listener; every connection is crawl traffic.
/// </summary>
public sealed class TcpOutboundConnector : IOutboundConnector
{
    private const int HandshakeTimeoutMs = 30_000;

    private readonly ProxyRoutingSettings _settings;
    private readonly UpstreamProxy? _upstream;

    /// <summary>Creates the connector for the given local routing settings.</summary>
    /// <param name="settings">User-supplied routing settings.</param>
    /// <exception cref="ArgumentException">The configured upstream proxy URL is invalid.</exception>
    public TcpOutboundConnector(ProxyRoutingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _upstream = string.IsNullOrWhiteSpace(settings.UpstreamProxyUrl)
            ? null
            : UpstreamProxyParser.Parse(settings.UpstreamProxyUrl);
    }

    /// <inheritdoc />
    public async Task<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);

        var route = ProxyRouteResolver.Resolve(host, _settings);
        if (route == ProxyRoute.Direct || _upstream is null)
        {
            return await ConnectDirectAsync(host, port, cancellationToken);
        }

        try
        {
            return await ConnectViaUpstreamAsync(host, port, cancellationToken);
        }
        catch (Exception ex) when (ex is SocketException or IOException or ProxyRoutingException or TimeoutException)
        {
            if (_settings.AllowDirectFallback)
            {
                return await ConnectDirectAsync(host, port, cancellationToken);
            }

            throw new ProxyRoutingException(
                $"Upstream proxy {_upstream.Host}:{_upstream.Port} failed for {host}:{port} and direct fallback is disabled.",
                ex);
        }
    }

    private static async Task<Stream> ConnectDirectAsync(string host, int port, CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, cancellationToken);
            return new OwnedNetworkStream(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task<Stream> ConnectViaUpstreamAsync(string host, int port, CancellationToken cancellationToken)
    {
        var upstream = _upstream!;
        var client = new TcpClient();
        try
        {
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshakeCts.CancelAfter(HandshakeTimeoutMs);
            var token = handshakeCts.Token;

            await client.ConnectAsync(upstream.Host, upstream.Port, token);
            Stream stream = new OwnedNetworkStream(client);

            if (string.Equals(upstream.Scheme, "https", StringComparison.Ordinal))
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions { TargetHost = upstream.Host },
                    token);
                stream = ssl;
            }

            if (upstream.IsSocks)
            {
                await SocksHandshakeAsync(stream, upstream, host, port, token);
            }
            else
            {
                await HttpConnectHandshakeAsync(stream, host, port, token);
            }

            return stream;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task HttpConnectHandshakeAsync(Stream stream, string host, int port, CancellationToken cancellationToken)
    {
        var request = Encoding.ASCII.GetBytes(
            $"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\n\r\n");
        await stream.WriteAsync(request, cancellationToken);

        var (statusLine, _) = await ReadHeadersAsync(stream, cancellationToken);
        var parts = statusLine.Split(' ', 3);
        if (parts.Length < 2 || !int.TryParse(parts[1], out var status) || status != 200)
        {
            throw new ProxyRoutingException($"Upstream proxy rejected CONNECT {host}:{port}: '{statusLine}'.");
        }
    }

    private static async Task SocksHandshakeAsync(Stream stream, UpstreamProxy upstream, string host, int port, CancellationToken cancellationToken)
    {
        if (string.Equals(upstream.Scheme, "socks4", StringComparison.Ordinal))
        {
            await Socks4ConnectAsync(stream, host, port, cancellationToken);
            return;
        }

        // SOCKS5 greeting: no-auth, plus username/password when the user supplied credentials.
        byte[] methods = upstream.HasCredentials ? [0x00, 0x02] : [0x00];
        var greeting = new byte[2 + methods.Length];
        greeting[0] = 0x05;
        greeting[1] = (byte)methods.Length;
        methods.CopyTo(greeting, 2);
        await stream.WriteAsync(greeting, cancellationToken);

        var selection = await ReadExactAsync(stream, 2, cancellationToken);
        if (selection[0] != 0x05 || selection[1] == 0xFF)
        {
            throw new ProxyRoutingException("Upstream SOCKS5 proxy rejected the offered authentication methods.");
        }

        if (selection[1] == 0x02)
        {
            var user = Encoding.UTF8.GetBytes(upstream.UserName ?? string.Empty);
            var pass = Encoding.UTF8.GetBytes(upstream.Password ?? string.Empty);
            var auth = new byte[3 + user.Length + pass.Length];
            auth[0] = 0x01;
            auth[1] = (byte)user.Length;
            user.CopyTo(auth, 2);
            auth[2 + user.Length] = (byte)pass.Length;
            pass.CopyTo(auth, 3 + user.Length);
            await stream.WriteAsync(auth, cancellationToken);

            var authReply = await ReadExactAsync(stream, 2, cancellationToken);
            if (authReply[1] != 0x00)
            {
                throw new ProxyRoutingException("Upstream SOCKS5 proxy rejected the supplied credentials.");
            }
        }
        else if (selection[1] != 0x00)
        {
            throw new ProxyRoutingException($"Upstream SOCKS5 proxy selected unsupported method 0x{selection[1]:x2}.");
        }

        // CONNECT with a domain name (ATYP 0x03): resolution stays with the upstream proxy.
        var hostBytes = Encoding.ASCII.GetBytes(host);
        var connect = new byte[7 + hostBytes.Length];
        connect[0] = 0x05;
        connect[1] = 0x01;
        connect[2] = 0x00;
        connect[3] = 0x03;
        connect[4] = (byte)hostBytes.Length;
        hostBytes.CopyTo(connect, 5);
        connect[5 + hostBytes.Length] = (byte)(port >> 8);
        connect[6 + hostBytes.Length] = (byte)(port & 0xFF);
        await stream.WriteAsync(connect, cancellationToken);

        var replyHead = await ReadExactAsync(stream, 4, cancellationToken);
        if (replyHead[0] != 0x05 || replyHead[1] != 0x00)
        {
            throw new ProxyRoutingException($"Upstream SOCKS5 proxy refused CONNECT {host}:{port} (code 0x{replyHead[1]:x2}).");
        }

        var boundLength = replyHead[3] switch
        {
            0x01 => 4,
            0x03 => (await ReadExactAsync(stream, 1, cancellationToken))[0],
            0x04 => 16,
            _ => throw new ProxyRoutingException($"Upstream SOCKS5 proxy returned an unknown address type 0x{replyHead[3]:x2}."),
        };
        await ReadExactAsync(stream, boundLength + 2, cancellationToken);
    }

    private static async Task Socks4ConnectAsync(Stream stream, string host, int port, CancellationToken cancellationToken)
    {
        // SOCKS4a: when the host is not a literal IPv4 address, send 0.0.0.x and
        // append the domain name so the upstream proxy resolves it.
        var isIpv4 = System.Net.IPAddress.TryParse(host, out var address)
            && address.AddressFamily == AddressFamily.InterNetwork;
        var ipBytes = isIpv4 ? address!.GetAddressBytes() : [0, 0, 0, 1];
        var hostBytes = isIpv4 ? [] : Encoding.ASCII.GetBytes(host);

        // Header (8) + empty user id NUL (1) + optional domain + trailing NUL.
        var request = new byte[9 + hostBytes.Length + (isIpv4 ? 0 : 1)];
        request[0] = 0x04;
        request[1] = 0x01;
        request[2] = (byte)(port >> 8);
        request[3] = (byte)(port & 0xFF);
        ipBytes.CopyTo(request, 4);
        request[8] = 0x00; // empty user id
        if (!isIpv4)
        {
            hostBytes.CopyTo(request, 9);
            request[9 + hostBytes.Length] = 0x00;
        }

        await stream.WriteAsync(request, cancellationToken);
        var reply = await ReadExactAsync(stream, 8, cancellationToken);
        if (reply[1] != 90)
        {
            throw new ProxyRoutingException($"Upstream SOCKS4 proxy refused CONNECT {host}:{port} (code {reply[1]}).");
        }
    }

    private static async Task<(string StatusLine, List<string> Headers)> ReadHeadersAsync(Stream stream, CancellationToken cancellationToken)
    {
        var statusLine = await ReadLineAsync(stream, cancellationToken);
        var headers = new List<string>();
        while (true)
        {
            var line = await ReadLineAsync(stream, cancellationToken);
            if (line.Length == 0)
            {
                break;
            }

            headers.Add(line);
            if (headers.Count > 100)
            {
                throw new ProxyRoutingException("Upstream proxy response exceeded the header bound.");
            }
        }

        return (statusLine, headers);
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken);
            if (read == 0)
            {
                throw new ProxyRoutingException("Upstream proxy closed the connection during handshake.");
            }

            if (buffer[0] == (byte)'\n')
            {
                return builder.ToString().TrimEnd('\r');
            }

            builder.Append((char)buffer[0]);
            if (builder.Length > 8192)
            {
                throw new ProxyRoutingException("Upstream proxy handshake line exceeded the size bound.");
            }
        }
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), cancellationToken);
            if (read == 0)
            {
                throw new ProxyRoutingException("Upstream proxy closed the connection during handshake.");
            }

            offset += read;
        }

        return buffer;
    }
}

/// <summary>
/// A network stream that owns its underlying <see cref="TcpClient"/> so tunnel
/// callers can dispose a single object.
/// </summary>
internal sealed class OwnedNetworkStream : Stream
{
    private readonly TcpClient _client;
    private readonly NetworkStream _inner;

    public OwnedNetworkStream(TcpClient client)
    {
        _client = client;
        _inner = client.GetStream();
    }

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => _inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => _inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _inner.WriteAsync(buffer, offset, count, cancellationToken);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.WriteAsync(buffer, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
            _client.Dispose();
        }

        base.Dispose(disposing);
    }
}
