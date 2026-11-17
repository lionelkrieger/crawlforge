using System.Net;
using System.Net.Sockets;
using CrawlForge.Security.Secrets;

namespace CrawlForge.Data.MySql.Tests;

/// <summary>
/// Disposable app-entropy protector fixture: a real <see cref="AppEntropySecretProtector"/>
/// over a fresh random key file in a temp directory, deleted on dispose.
/// </summary>
internal sealed class TempProtector : IDisposable
{
    private readonly string _directory;

    public TempProtector()
    {
        _directory = Path.Combine(Path.GetTempPath(), "cf-mysql-tests", Guid.NewGuid().ToString("N"));
        KeyStore = new EntropyKeyStore(Path.Combine(_directory, "entropy.key"));
        Protector = new AppEntropySecretProtector(KeyStore);
    }

    public EntropyKeyStore KeyStore { get; }

    public AppEntropySecretProtector Protector { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; never fail a test on it.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort temp cleanup; never fail a test on it.
        }
    }
}

/// <summary>Finds a loopback TCP port that is closed (nothing listening).</summary>
internal static class ClosedLoopbackEndpoint
{
    public static int GetClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
