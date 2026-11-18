using System.Reflection;
using System.Text.RegularExpressions;
using CrawlForge.ContractConformance.Tests.Harness;
using CrawlForge.Security.Handshake;
using Xunit;

namespace CrawlForge.ContractConformance.Tests;

/// <summary>
/// Electron IPC bridge conformance (FS-CRAWL-001 §12 / shell spec §2.1/§2.2/
/// §8.1; channel names frozen by the accepted WP-06-02 shell). Source-level
/// cross-checks over the THREE frozen sources that must agree: the typed
/// bridge contract (preload/bridge.ts), the inline sandboxed preload
/// (preload.ts), and the main-process wiring (main.ts wireIpc). The Electron
/// app itself is owned by cf-wp-08-04; this suite never starts it.
/// </summary>
public sealed class IpcContractConformanceTests
{
    private static readonly string BridgeTs = File.ReadAllText(RepoPaths.ElectronBridgeTs);
    private static readonly string PreloadTs = File.ReadAllText(RepoPaths.ElectronPreloadTs);
    private static readonly string MainTs = File.ReadAllText(RepoPaths.ElectronMainTs);

    private static readonly IReadOnlyDictionary<string, string> BridgeChannels = ParseBridgeChannels();

    /// <summary>The frozen IPC_CHANNELS block carries exactly the nine locked channels.</summary>
    [Fact]
    public void BridgeChannels_FrozenNine_MatchSpec()
    {
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "crawlforge:file-dialog",
            "crawlforge:notification",
            "crawlforge:taskbar-progress",
            "crawlforge:window-minimize",
            "crawlforge:window-maximize",
            "crawlforge:window-close",
            "crawlforge:backend-restart",
            "crawlforge:diagnostics-purge",
            "crawlforge:backend-status"
        };
        Assert.Equal(9, BridgeChannels.Count);
        Assert.Equal(expected, BridgeChannels.Values.ToHashSet(StringComparer.Ordinal));
        Assert.All(BridgeChannels.Values, channel => Assert.StartsWith("crawlforge:", channel, StringComparison.Ordinal));
    }

    /// <summary>
    /// The inline preload uses exactly the frozen bridge channels plus the two
    /// handshake literals (crawlforge:get-bridge, crawlforge:hydrated) — 11 total.
    /// </summary>
    [Fact]
    public void PreloadInline_ChannelsMatchBridge()
    {
        var preloadChannels = ChannelLiterals(PreloadTs);
        var expected = BridgeChannels.Values.ToHashSet(StringComparer.Ordinal);
        expected.Add("crawlforge:get-bridge");
        expected.Add("crawlforge:hydrated");
        Assert.Equal(expected, preloadChannels);
        Assert.Equal(11, preloadChannels.Count);
    }

    /// <summary>
    /// Every channel is wired in main.ts with the matching IPC kind: preload
    /// invoke ↔ ipcMain.handle, preload send ↔ ipcMain.on, and backend-status
    /// is pushed main→renderer via webContents.send.
    /// </summary>
    [Fact]
    public void MainProcess_HandlesEveryBridgeChannel()
    {
        // Resolve IPC_CHANNELS.<member> references in main.ts to channel names.
        var mainHandles = new HashSet<string>(StringComparer.Ordinal);
        var mainOns = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(MainTs, "ipcMain\\.(handle|on)\\(\\s*(IPC_CHANNELS\\.(\\w+)|'([^']+)')"))
        {
            var channel = match.Groups[3].Success
                ? BridgeChannels[match.Groups[3].Value]
                : match.Groups[4].Value;
            if (match.Groups[1].Value == "handle")
            {
                mainHandles.Add(channel);
            }
            else
            {
                mainOns.Add(channel);
            }
        }

        // Preload side: invoke vs send per channel.
        var preloadInvokes = new HashSet<string>(StringComparer.Ordinal);
        var preloadSends = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(PreloadTs, "ipcRenderer\\.(invoke|send)\\(\\s*'([^']+)'"))
        {
            (match.Groups[1].Value == "invoke" ? preloadInvokes : preloadSends).Add(match.Groups[2].Value);
        }

        foreach (var channel in preloadInvokes)
        {
            Assert.Contains(channel, mainHandles);
        }

        foreach (var channel in preloadSends)
        {
            Assert.Contains(channel, mainOns);
        }

        // backend-status: renderer subscribes (on), main pushes (webContents.send).
        Assert.Contains("ipcRenderer.on('crawlforge:backend-status'", PreloadTs, StringComparison.Ordinal);
        Assert.Contains("webContents.send(IPC_CHANNELS.backendStatus", MainTs, StringComparison.Ordinal);

        // All nine bridge channels are wired (handle or on) plus the two literals.
        var wired = mainHandles.Union(mainOns).ToHashSet(StringComparer.Ordinal);
        foreach (var channel in BridgeChannels.Values.Where(c => c != "crawlforge:backend-status"))
        {
            Assert.Contains(channel, wired);
        }

        Assert.Contains("crawlforge:get-bridge", mainHandles);
        Assert.Contains("crawlforge:hydrated", mainOns);
    }

    /// <summary>
    /// Descriptor validation agrees across the boundary: the backend publishes
    /// {version,port,sessionToken,createdAtUtc} (HandshakeFileRecord, camelCase
    /// via JsonSerializerDefaults.Web); createBridge/parseDescriptor and the
    /// inline preload validator enforce the same shape (version 1, integer port
    /// 1..65535, non-empty token, string createdAtUtc).
    /// </summary>
    [Fact]
    public void BridgeDescriptor_ValidationContract()
    {
        // Backend record shape (private nested record; reflection is read-only).
        var recordType = typeof(LoopbackPortHandshake).GetNestedType("HandshakeFileRecord", BindingFlags.NonPublic);
        Assert.NotNull(recordType);
        var properties = recordType!.GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "CreatedAtUtc", "Port", "SessionToken", "Version" }, properties);
        Assert.Equal(1, LoopbackPortHandshake.HandshakeFileVersion);

        // TS descriptor interface (preload.ts) carries the camelCase mirror.
        foreach (var field in new[] { "version: number", "port: number", "sessionToken: string", "createdAtUtc: string" })
        {
            Assert.Contains(field, PreloadTs, StringComparison.Ordinal);
        }

        // Both validators enforce the same predicates (bridge.ts rejects the
        // empty token explicitly; the inline preload requires length > 0).
        foreach (var source in new[] { BridgeTs, PreloadTs })
        {
            Assert.Contains("version === 1", source, StringComparison.Ordinal);
            Assert.Contains("Number.isInteger", source, StringComparison.Ordinal);
            Assert.Contains("65535", source, StringComparison.Ordinal);
            Assert.Contains("createdAtUtc", source, StringComparison.Ordinal);
        }

        Assert.Contains(".length === 0", BridgeTs, StringComparison.Ordinal);
        Assert.Contains(".length > 0", PreloadTs, StringComparison.Ordinal);

        // createBridge rejects unsupported versions / invalid ports / empty tokens.
        Assert.Contains("Unsupported handshake version.", BridgeTs, StringComparison.Ordinal);
        Assert.Contains("Handshake port is invalid.", BridgeTs, StringComparison.Ordinal);
        Assert.Contains("Handshake session token is empty.", BridgeTs, StringComparison.Ordinal);
    }

    /// <summary>
    /// The shell surface matches the frozen contract: CrawlForgeShellBridge's
    /// eight typed members plus the additive onBackendStatus/signalHydrated in
    /// the preload; the core window.crawlForge stays {backendBaseUrl,
    /// getSessionToken} (accepted WP-06-02 contract, zero delta).
    /// </summary>
    [Fact]
    public void ShellBridge_MembersMatchContract()
    {
        var interfaceMatch = Regex.Match(BridgeTs, "interface CrawlForgeShellBridge \\{(?<body>[^}]*)\\}", RegexOptions.Singleline);
        Assert.True(interfaceMatch.Success, "CrawlForgeShellBridge interface not found");
        var members = Regex.Matches(interfaceMatch.Groups["body"].Value, "^\\s*(\\w+)\\(", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(
            new[] { "showFileDialog", "showNotification", "setTaskbarProgress", "minimizeWindow", "maximizeWindow", "closeWindow", "restartBackend", "purgeDiagnostics" },
            members);

        // Preload exposes the eight plus the two additive members.
        var exposeMatch = Regex.Match(PreloadTs, "exposeInMainWorld\\('crawlForgeShell', \\{(?<body>.*?)\\n\\}\\)", RegexOptions.Singleline);
        Assert.True(exposeMatch.Success, "crawlForgeShell exposure not found");
        var exposed = Regex.Matches(exposeMatch.Groups["body"].Value, "^\\s*(\\w+):", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(10, exposed.Count);
        foreach (var member in members)
        {
            Assert.Contains(member, exposed);
        }

        Assert.Contains("onBackendStatus", exposed);
        Assert.Contains("signalHydrated", exposed);

        // Core contract untouched.
        Assert.Contains("backendBaseUrl", PreloadTs, StringComparison.Ordinal);
        Assert.Contains("getSessionToken", PreloadTs, StringComparison.Ordinal);
        Assert.Contains("backendBaseUrl: baseUrl", BridgeTs, StringComparison.Ordinal);
    }

    /// <summary>FileDialogRequest modes are the locked three; main handles each.</summary>
    [Fact]
    public void FileDialogRequest_ModesLocked()
    {
        Assert.Contains("'open-file' | 'open-directory' | 'save-file'", BridgeTs, StringComparison.Ordinal);
        Assert.Contains("request.mode === 'save-file'", MainTs, StringComparison.Ordinal);
        Assert.Contains("request.mode === 'open-directory'", MainTs, StringComparison.Ordinal);
        Assert.Contains("['openFile']", MainTs, StringComparison.Ordinal);
    }

    /// <summary>Every IPC channel literal at an IPC call site uses the crawlforge: prefix.</summary>
    [Fact]
    public void ChannelNaming_CrawlForgePrefix()
    {
        // Channel definitions in the frozen bridge contract.
        Assert.All(BridgeChannels.Values, channel => Assert.StartsWith("crawlforge:", channel, StringComparison.Ordinal));

        // Channel literals at ipcRenderer/ipcMain call sites in preload + main.
        foreach (var source in new[] { PreloadTs, MainTs })
        {
            foreach (Match match in Regex.Matches(source, "ipc(?:Renderer|Main)\\.(?:invoke|send|on|handle|removeListener)\\(\\s*'([^']+)'"))
            {
                Assert.StartsWith("crawlforge:", match.Groups[1].Value, StringComparison.Ordinal);
            }
        }
    }

    private static IReadOnlyDictionary<string, string> ParseBridgeChannels()
    {
        var block = Regex.Match(BridgeTs, "IPC_CHANNELS\\s*=\\s*Object\\.freeze\\(\\{(?<body>[^}]*)\\}", RegexOptions.Singleline);
        if (!block.Success)
        {
            throw new InvalidOperationException("IPC_CHANNELS block not found in bridge.ts");
        }

        var channels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(block.Groups["body"].Value, "(\\w+):\\s*'(crawlforge:[^']+)'"))
        {
            channels[match.Groups[1].Value] = match.Groups[2].Value;
        }

        return channels;
    }

    private static HashSet<string> ChannelLiterals(string source) =>
        Regex.Matches(source, "'(crawlforge:[^']+)'")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
}
