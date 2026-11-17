namespace CrawlForge.Contracts.Security;

/// <summary>
/// Loopback port handshake between the desktop shell and the local backend
/// (FS-CRAWL-001 §2.2): the backend binds loopback only and publishes a
/// per-launch session token through a local handshake file. The token is a
/// locally generated credential — no remote issuance, rotation, or revocation
/// exists (exclusion row 4).
/// </summary>
public interface ILoopbackPortHandshake
{
    /// <summary>Acquires a loopback port and publishes the handshake.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Handshake details for the shell.</returns>
    Task<LoopbackHandshake> StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Retires the handshake and releases the session token.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the handshake is retired.</returns>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Validates a presented per-launch session token.</summary>
    /// <param name="token">Token presented by the shell.</param>
    /// <returns>True when the token matches the current launch.</returns>
    bool ValidateSessionToken(string token);
}

/// <summary>
/// Loopback handshake details.
/// </summary>
/// <param name="Port">Loopback port the backend listens on.</param>
/// <param name="SessionToken">Per-launch session token (locally generated).</param>
/// <param name="HandshakeFilePath">Local handshake file path read by the shell.</param>
public sealed record LoopbackHandshake(int Port, string SessionToken, string HandshakeFilePath);
