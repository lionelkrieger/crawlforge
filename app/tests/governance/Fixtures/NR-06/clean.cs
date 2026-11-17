using Microsoft.Extensions.Logging;

namespace Fixture.Nr06;

/// <summary>Local structured logs + local native minidumps only; nothing is transmitted.</summary>
public sealed class LocalDiagnostics
{
    private readonly ILogger<LocalDiagnostics> _logger;

    public LocalDiagnostics(ILogger<LocalDiagnostics> logger) => _logger = logger;

    public void RecordLocalEvent(string name) => _logger.LogInformation("Local event {EventName}.", name);

    public static void WriteLocalMiniDump(string path) => MiniDump.Write(path);
}

public static class MiniDump
{
    public static void Write(string path)
    {
        // Local native minidump, user-viewable and purgeable; never transmitted.
    }
}
