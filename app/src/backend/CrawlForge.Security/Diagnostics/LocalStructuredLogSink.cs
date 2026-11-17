using System.Globalization;
using System.Text;
using System.Text.Json;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Security.Diagnostics;

/// <summary>
/// Local structured logging sink (FS-CRAWL-001 §10.5): writes JSON-line events
/// to user-viewable log files under the local diagnostics directory. Events
/// carry no identity dimensions — the sink never enriches with ambient
/// user/machine/account context, and caller properties pass through
/// <see cref="IdentityDimensionFilter"/> (privacy invariant, exclusion row 6).
/// </summary>
/// <remarks>
/// <para>
/// One file per UTC day: <c>logs/structured-yyyy-MM-dd.jsonl</c>. Each new file
/// is registered in the shared <see cref="DiagnosticArtifactRegistry"/> as a
/// <see cref="DiagnosticArtifactKind.Log"/> artifact, so the diagnostic-artifacts
/// purge API (<see cref="Contracts.Security.ICrashDumpHandler.PurgeArtifactsAsync"/>,
/// consumed by the settings surface and recovery screen) lists and purges logs
/// alongside minidumps. After a purge the next write recreates and re-registers
/// the current day's file.
/// </para>
/// <para>
/// Logs are local-only: this type contains no network code path and nothing is
/// ever transmitted automatically. Intended as a process-wide singleton.
/// </para>
/// </remarks>
public sealed class LocalStructuredLogSink
{
    private static readonly JsonSerializerOptions LineOptions = new(JsonSerializerDefaults.Web);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly DiagnosticArtifactRegistry _registry;
    private readonly object _gate = new();
    private string? _currentFilePath;
    private DateOnly _currentFileDate;

    /// <summary>Creates a log sink writing into the given local registry.</summary>
    /// <param name="registry">Shared local diagnostic artifact registry.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> is null.</exception>
    public LocalStructuredLogSink(DiagnosticArtifactRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>
    /// Appends one structured event to the current local log file. Identity
    /// dimensions in <paramref name="properties"/> are dropped; no ambient
    /// identity context is ever added.
    /// </summary>
    /// <param name="level">Event severity.</param>
    /// <param name="message">Event message.</param>
    /// <param name="properties">Optional local technical properties.</param>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    public void Write(LocalLogLevel level, string message, IReadOnlyDictionary<string, object?>? properties = null)
    {
        ArgumentNullException.ThrowIfNull(message);

        IReadOnlyDictionary<string, object?> sanitized = IdentityDimensionFilter.Sanitize(properties);
        var line = new LogLine(
            DateTimeOffset.UtcNow,
            level.ToString(),
            message,
            sanitized.Count == 0 ? null : sanitized);
        string json = JsonSerializer.Serialize(line, LineOptions);

        lock (_gate)
        {
            string path = EnsureCurrentFile();
            File.AppendAllText(path, json + Environment.NewLine, Utf8NoBom);
        }
    }

    private string EnsureCurrentFile()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (_currentFilePath is null || _currentFileDate != today || !File.Exists(_currentFilePath))
        {
            _currentFileDate = today;
            _currentFilePath = Path.Combine(
                _registry.LogDirectory,
                "structured-" + today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");

            Directory.CreateDirectory(_registry.LogDirectory);
            if (!File.Exists(_currentFilePath))
            {
                File.WriteAllText(_currentFilePath, string.Empty, Utf8NoBom);
            }

            _registry
                .RegisterAsync(_currentFilePath, DiagnosticArtifactKind.Log)
                .GetAwaiter()
                .GetResult();
        }

        return _currentFilePath;
    }

    private sealed record LogLine(
        DateTimeOffset TimestampUtc,
        string Level,
        string Message,
        IReadOnlyDictionary<string, object?>? Properties);
}