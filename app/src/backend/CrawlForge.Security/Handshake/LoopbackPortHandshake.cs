using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CrawlForge.Contracts.Security;

namespace CrawlForge.Security.Handshake;

/// <summary>
/// Loopback port handshake between the desktop shell and the local backend
/// (FS-CRAWL-001 §2.2): acquires a loopback-only port, generates a per-launch
/// random session token, and publishes both to a local handshake file in the
/// per-user application data directory for supervisor discovery.
/// </summary>
/// <remarks>
/// <para>
/// Port strategy (spec §2.2): the configured preferred loopback port is probed
/// first; when occupied, an operating-system-assigned ephemeral loopback port
/// is used instead. The probe binds <see cref="IPAddress.Loopback"/> only —
/// never a wildcard or external address — and releases the probe socket before
/// publishing, so the backend host must bind the published port immediately
/// after <see cref="StartAsync"/> completes (host wiring is a later phase).
/// </para>
/// <para>
/// The session token is 32 app-generated random bytes (Base64Url). It is not
/// derived from hardware, OS stores, or any persistent machine identity
/// (exclusion row 1), is never issued/rotated/revoked remotely (exclusion
/// row 4), and never leaves the machine. The handshake file carries only
/// <c>version</c>, <c>port</c>, <c>sessionToken</c>, and <c>createdAtUtc</c> —
/// no identity dimensions (privacy invariant). See <c>Handshake/README.md</c>
/// for the supervisor discovery contract.
/// </para>
/// </remarks>
public sealed class LoopbackPortHandshake : ILoopbackPortHandshake
{
    /// <summary>Schema version of the published handshake file.</summary>
    public const int HandshakeFileVersion = 1;

    /// <summary>Length of the per-launch session token in random bytes.</summary>
    public const int SessionTokenBytes = 32;

    private static readonly JsonSerializerOptions FileOptions = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly int _preferredPort;
    private string? _sessionToken;
    private bool _published;

    /// <summary>Creates a handshake publishing to the given local file.</summary>
    /// <param name="handshakeFilePath">
    /// Local handshake file path read by the shell. Use
    /// <see cref="DefaultHandshakeFilePath"/> for the per-user default location.
    /// </param>
    /// <param name="preferredPort">
    /// Preferred loopback port attempted first; 0 selects an ephemeral port
    /// directly. When occupied, an OS-assigned ephemeral port is used.
    /// </param>
    /// <exception cref="ArgumentException">The path is null, empty, or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The preferred port is outside 0–65535.</exception>
    public LoopbackPortHandshake(string handshakeFilePath, int preferredPort = 0)
    {
        if (string.IsNullOrWhiteSpace(handshakeFilePath))
        {
            throw new ArgumentException("Handshake file path must be a non-empty local path.", nameof(handshakeFilePath));
        }

        if (preferredPort is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(preferredPort), preferredPort, "Preferred port must be between 0 and 65535.");
        }

        HandshakeFilePath = Path.GetFullPath(handshakeFilePath);
        _preferredPort = preferredPort;
    }

    /// <summary>
    /// Default handshake file location: <c>loopback-handshake.json</c> inside the
    /// per-user CrawlForge application-data directory (spec §2.2 port descriptor).
    /// </summary>
    public static string DefaultHandshakeFilePath =>
        Path.Combine(CrawlForgePaths.GetPerUserDataDirectory(), "loopback-handshake.json");

    /// <summary>Absolute local path of the published handshake file.</summary>
    public string HandshakeFilePath { get; }

    /// <summary>True while a handshake is published (between StartAsync and StopAsync).</summary>
    public bool IsStarted => Volatile.Read(ref _published);

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">A handshake is already published.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public async Task<LoopbackHandshake> StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_published)
            {
                throw new InvalidOperationException(
                    "The loopback handshake is already started; call StopAsync before starting again.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            string token = GenerateSessionToken();
            int port = AcquireLoopbackPort(_preferredPort);
            var record = new HandshakeFileRecord(HandshakeFileVersion, port, token, DateTimeOffset.UtcNow);
            PublishHandshakeFile(record);

            _sessionToken = token;
            _published = true;
            return new LoopbackHandshake(port, token, HandshakeFilePath);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_published)
            {
                return;
            }

            TryDeleteHandshakeFile();
            _sessionToken = null;
            _published = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public bool ValidateSessionToken(string token)
    {
        string? current = Volatile.Read(ref _sessionToken);
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(current))
        {
            return false;
        }

        byte[] presented = Encoding.UTF8.GetBytes(token);
        byte[] expected = Encoding.UTF8.GetBytes(current);
        return CryptographicOperations.FixedTimeEquals(presented, expected);
    }

    private static string GenerateSessionToken()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(SessionTokenBytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static int AcquireLoopbackPort(int preferredPort)
    {
        if (preferredPort != 0)
        {
            int? preferred = TryProbeLoopbackPort(preferredPort);
            if (preferred.HasValue)
            {
                return preferred.Value;
            }
        }

        int? ephemeral = TryProbeLoopbackPort(0);
        if (!ephemeral.HasValue)
        {
            throw new InvalidOperationException("Unable to acquire a free loopback port for the backend.");
        }

        return ephemeral.Value;
    }

    private static int? TryProbeLoopbackPort(int port)
    {
        // Loopback-only probe: never binds a wildcard or externally reachable address.
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            LoopbackGuard.EnsureLoopback(endpoint);
            return endpoint.Port;
        }
        catch (SocketException)
        {
            // Port occupied or unavailable: fall back to an ephemeral port.
            return null;
        }
        finally
        {
            listener.Stop();
        }
    }

    private void PublishHandshakeFile(HandshakeFileRecord record)
    {
        string? directory = Path.GetDirectoryName(HandshakeFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Atomic publish so the supervisor never observes a half-written descriptor.
        string tempPath = HandshakeFilePath + "." + Path.GetRandomFileName() + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(record, FileOptions), new UTF8Encoding(false));
        File.Move(tempPath, HandshakeFilePath, overwrite: true);
    }

    private void TryDeleteHandshakeFile()
    {
        try
        {
            if (File.Exists(HandshakeFilePath))
            {
                File.Delete(HandshakeFilePath);
            }
        }
        catch (IOException)
        {
            // Retirement is best-effort on disk; the in-memory token is invalidated regardless.
        }
        catch (UnauthorizedAccessException)
        {
            // Retirement is best-effort on disk; the in-memory token is invalidated regardless.
        }
    }

    private sealed record HandshakeFileRecord(int Version, int Port, string SessionToken, DateTimeOffset CreatedAtUtc);
}