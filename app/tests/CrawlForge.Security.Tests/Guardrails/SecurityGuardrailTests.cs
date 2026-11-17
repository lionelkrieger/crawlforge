using System.Reflection;
using CrawlForge.Security.Secrets;
using Xunit;

namespace CrawlForge.Security.Tests.Guardrails;

/// <summary>
/// Structural clean-room guardrails for the security runtime (manual §6):
/// no transmission code path exists anywhere in CrawlForge.Security (exclusion
/// row 6 — zero automatic transmission of diagnostics), no machine-identity or
/// OS-store reads (exclusion row 1 — app-entropy only), and sockets appear only
/// in the loopback handshake, bound to loopback exclusively.
/// </summary>
public sealed class SecurityGuardrailTests
{
    private static readonly string[] ForbiddenAssemblyPrefixes =
    {
        "System.Net.Http",
        "System.Net.HttpListener",
        "System.Net.Mail",
        "System.Net.NameResolution",
        "System.Net.WebSockets",
        "Microsoft.Win32.Registry",
        "System.Management",
        "System.DirectoryServices",
    };

    private static readonly string[] IdentityTokens =
    {
        "Microsoft.Win32",
        "RegistryKey",
        "Registry.GetValue",
        "ManagementObjectSearcher",
        "System.Management",
        "Environment.MachineName",
        "Environment.UserName",
        "Environment.UserDomainName",
        "MachineGuid",
        "Win32_",
        "Dns.",
        "GetHostEntry",
    };

    private static readonly string[] TransmissionTokens =
    {
        "HttpClient",
        "WebClient",
        "WebRequest",
        "HttpWebRequest",
        "TcpClient",
        "UdpClient",
        "NetworkStream",
        "WebSocket",
        "System.Net.Http",
        "System.Net.Mail",
        "System.Net.NameResolution",
    };

    private static readonly string[] SocketTokens =
    {
        "TcpListener",
        "IPAddress",
        "IPEndPoint",
        "System.Net.Sockets",
        "Socket",
    };

    [Fact]
    public void SecurityAssembly_ReferencesNoTransmissionOrIdentityAssemblies()
    {
        Assembly assembly = typeof(AppEntropySecretProtector).Assembly;

        foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
        {
            string name = reference.Name ?? string.Empty;
            foreach (string forbidden in ForbiddenAssemblyPrefixes)
            {
                Assert.False(
                    name.StartsWith(forbidden, StringComparison.OrdinalIgnoreCase),
                    $"CrawlForge.Security references forbidden assembly '{name}'.");
            }
        }
    }

    [Fact]
    public void SecuritySources_ContainNoTransmissionOrIdentityApis()
    {
        string sourceRoot = FindSecuritySourceRoot();
        string[] files = Directory.GetFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildArtifact(sourceRoot, f))
            .ToArray();
        Assert.True(files.Length > 0, "Source scan found no files; guard would be vacuous.");

        foreach (string file in files)
        {
            string relative = Path.GetRelativePath(sourceRoot, file);
            string text = File.ReadAllText(file);
            bool isHandshake = relative.StartsWith("Handshake" + Path.DirectorySeparatorChar, StringComparison.Ordinal);

            foreach (string token in IdentityTokens)
            {
                Assert.False(
                    text.Contains(token, StringComparison.Ordinal),
                    $"{relative}: forbidden identity/OS-store token '{token}'.");
            }

            foreach (string token in TransmissionTokens)
            {
                Assert.False(
                    text.Contains(token, StringComparison.Ordinal),
                    $"{relative}: forbidden transmission token '{token}'.");
            }

            if (isHandshake)
            {
                // The handshake may probe sockets, but never a wildcard bind.
                Assert.False(
                    text.Contains("IPAddress.Any", StringComparison.Ordinal),
                    $"{relative}: wildcard bind is forbidden (loopback only).");
                Assert.False(
                    text.Contains("IPAddress.IPv6Any", StringComparison.Ordinal),
                    $"{relative}: wildcard bind is forbidden (loopback only).");
            }
            else
            {
                // Secrets and diagnostics must contain no socket code at all.
                foreach (string token in SocketTokens)
                {
                    Assert.False(
                        text.Contains(token, StringComparison.Ordinal),
                        $"{relative}: socket token '{token}' outside the loopback handshake.");
                }
            }
        }
    }

    private static bool IsBuildArtifact(string sourceRoot, string file)
    {
        string relative = Path.GetRelativePath(sourceRoot, file);
        return relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string FindSecuritySourceRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "CrawlForge.sln")))
            {
                return Path.Combine(directory, "src", "backend", "CrawlForge.Security");
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("Could not locate CrawlForge.sln above the test binary.");
    }
}