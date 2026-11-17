using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Intelligence;

namespace CrawlForge.Intelligence.LogAnalyzer;

/// <summary>
/// Live import counters (FS-CRAWL-002 §11.2): malformed lines are counted and
/// skipped with a bounded error sample; out-of-window lines are counted
/// separately. Persisted into log_imports.counters_json at finalization.
/// </summary>
public sealed class LogImportCounters
{
    /// <summary>Total lines read from the source.</summary>
    public long LinesParsed { get; internal set; }

    /// <summary>Lines accepted into aggregation (in window, well-formed).</summary>
    public long LinesAccepted { get; internal set; }

    /// <summary>Malformed lines discarded.</summary>
    public long LinesRejected { get; internal set; }

    /// <summary>Well-formed lines outside the analysis window.</summary>
    public long LinesOutOfWindow { get; internal set; }

    /// <summary>Bounded sample of rejected lines (truncated).</summary>
    public List<string> ErrorSamples { get; } = [];
}

/// <summary>
/// Local parser options: bounded error sample and the analysis time window
/// (FS-CRAWL-002 §11.2). Custom field lists support user-described formats.
/// </summary>
/// <param name="MaxErrorSamples">Maximum rejected-line samples retained.</param>
/// <param name="WindowStart">Inclusive window start (UTC), when selected.</param>
/// <param name="WindowEnd">Exclusive window end (UTC), when selected.</param>
/// <param name="CustomFields">W3C-style field list for LogFileFormat.Custom.</param>
public sealed record LogParserOptions(
    int MaxErrorSamples = 100,
    DateTimeOffset? WindowStart = null,
    DateTimeOffset? WindowEnd = null,
    IReadOnlyList<string>? CustomFields = null);

/// <summary>
/// Local streaming-parser surface extending the locked contract with the
/// discard counters required by §11.2 (the locked ILogFileParser shape carries
/// entries only). The import service consumes this surface.
/// </summary>
public interface IStreamingLogFileParser : ILogFileParser
{
    /// <summary>Live counters for the most recent parse pass.</summary>
    LogImportCounters Counters { get; }

    /// <summary>Applies per-import options (window, error-sample bound) before the next pass.</summary>
    /// <param name="options">The options for the next parse pass.</param>
    void Configure(LogParserOptions options);
}

/// <summary>
/// Offline streaming log parser (FS-CRAWL-002 §11.2): Apache/Nginx Combined
/// (Common tolerated as a subset), IIS W3C with header-driven field mapping,
/// and Cloudflare JSON/CSV exports. Gzip detected by magic bytes. Lines stream
/// one at a time with bounded memory — file size is bounded only by disk.
/// Timestamps normalize to UTC; malformed lines are counted and skipped.
/// </summary>
public sealed class LogFileParser : IStreamingLogFileParser
{
    private static readonly Regex CombinedPattern = new(
        @"^(\S+) \S+ \S+ \[([^\]]+)\] ""(\S+) (\S+)[^""]*"" (\d{3}) (\S+)(?: ""([^""]*)"" ""([^""]*)"")?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex TimestampPattern = new(
        @"^\d{2}/[A-Za-z]{3}/\d{4}:\d{2}:\d{2}:\d{2} [+-]\d{4}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private LogParserOptions _options;

    /// <summary>Creates the parser.</summary>
    /// <param name="options">Local options; null uses defaults.</param>
    public LogFileParser(LogParserOptions? options = null)
    {
        _options = options ?? new LogParserOptions();
    }

    /// <inheritdoc />
    public LogImportCounters Counters { get; private set; } = new();

    /// <inheritdoc />
    public void Configure(LogParserOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<LogEntry> ParseAsync(
        Stream logStream,
        LogFileFormat format,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Counters = new LogImportCounters();
        var counters = Counters;

        // Gzip magic-byte detection (1f 8b); the peeked bytes are replayed.
        var peeked = new byte[2];
        var peekRead = await ReadAtLeastAsync(logStream, peeked, cancellationToken);
        Stream effective = peekRead == 2 && peeked[0] == 0x1F && peeked[1] == 0x8B
            ? new GZipStream(new PrefixStream(peeked, peekRead, logStream), CompressionMode.Decompress)
            : new PrefixStream(peeked, peekRead, logStream);

        using var reader = new StreamReader(effective, leaveOpen: true);

        if (format == LogFileFormat.IisW3c || format == LogFileFormat.Custom)
        {
            await foreach (var entry in ParseW3cAsync(reader, format, counters, cancellationToken))
            {
                yield return entry;
            }
        }
        else if (format == LogFileFormat.CloudflareExport)
        {
            await foreach (var entry in ParseCloudflareAsync(reader, counters, cancellationToken))
            {
                yield return entry;
            }
        }
        else
        {
            await foreach (var entry in ParseCombinedAsync(reader, counters, cancellationToken))
            {
                yield return entry;
            }
        }
    }

    private async IAsyncEnumerable<LogEntry> ParseCombinedAsync(
        StreamReader reader,
        LogImportCounters counters,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            counters.LinesParsed++;
            var entry = TryParseCombined(line);
            if (entry is null)
            {
                Reject(counters, line);
                continue;
            }

            if (InWindow(entry.Timestamp))
            {
                counters.LinesAccepted++;
                yield return entry;
            }
            else
            {
                counters.LinesOutOfWindow++;
            }
        }
    }

    private async IAsyncEnumerable<LogEntry> ParseW3cAsync(
        StreamReader reader,
        LogFileFormat format,
        LogImportCounters counters,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IReadOnlyList<string>? fields = format == LogFileFormat.Custom ? _options.CustomFields : null;
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (line.StartsWith('#'))
            {
                if (line.StartsWith("#Fields:", StringComparison.OrdinalIgnoreCase))
                {
                    fields = line["#Fields:".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                }

                continue; // directives are not data lines
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            counters.LinesParsed++;
            var entry = fields is null ? null : TryParseW3cLine(line, fields);
            if (entry is null)
            {
                Reject(counters, line);
                continue;
            }

            if (InWindow(entry.Timestamp))
            {
                counters.LinesAccepted++;
                yield return entry;
            }
            else
            {
                counters.LinesOutOfWindow++;
            }
        }
    }

    private async IAsyncEnumerable<LogEntry> ParseCloudflareAsync(
        StreamReader reader,
        LogImportCounters counters,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? line;
        string[]? csvFields = null;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            counters.LinesParsed++;
            LogEntry? entry;
            if (line.TrimStart().StartsWith('{'))
            {
                entry = TryParseCloudflareJson(line);
            }
            else
            {
                if (csvFields is null)
                {
                    csvFields = SplitCsvLine(line).ToArray();
                    counters.LinesParsed--; // header is not a data line
                    continue;
                }

                entry = TryParseCloudflareCsv(line, csvFields);
            }

            if (entry is null)
            {
                Reject(counters, line);
                continue;
            }

            if (InWindow(entry.Timestamp))
            {
                counters.LinesAccepted++;
                yield return entry;
            }
            else
            {
                counters.LinesOutOfWindow++;
            }
        }
    }

    private static LogEntry? TryParseCombined(string line)
    {
        try
        {
            var match = CombinedPattern.Match(line);
            if (!match.Success)
            {
                return null;
            }

            var timestampText = match.Groups[2].Value;
            if (!TimestampPattern.IsMatch(timestampText) ||
                !DateTimeOffset.TryParseExact(
                    timestampText,
                    "dd/MMM/yyyy:HH:mm:ss zzz",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var timestamp))
            {
                return null;
            }

            if (!int.TryParse(match.Groups[5].Value, out var status))
            {
                return null;
            }

            var bytesText = match.Groups[6].Value;
            long? bytes = long.TryParse(bytesText, out var bytesValue) ? bytesValue : null;
            var userAgent = match.Groups[8].Success && match.Groups[8].Value != "-" ? match.Groups[8].Value : null;

            return new LogEntry(
                timestamp.ToUniversalTime(),
                match.Groups[1].Value,
                match.Groups[3].Value,
                match.Groups[4].Value,
                status,
                userAgent,
                bytes);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static LogEntry? TryParseW3cLine(string line, IReadOnlyList<string> fields)
    {
        var values = SplitW3cLine(line);
        if (values.Count != fields.Count)
        {
            return null;
        }

        string? Get(string name)
        {
            var index = -1;
            for (var i = 0; i < fields.Count; i++)
            {
                if (string.Equals(fields[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }

            return index >= 0 && values[index] != "-" ? values[index] : null;
        }

        var date = Get("date");
        var time = Get("time");
        if (date is null ||
            !DateTimeOffset.TryParse(
                $"{date}T{time ?? "00:00:00"}Z",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestamp))
        {
            return null;
        }

        var method = Get("cs-method") ?? Get("cs(Method)");
        var path = Get("cs-uri-stem") ?? Get("cs(UriStem)");
        var query = Get("cs-uri-query") ?? Get("cs(UriQuery)");
        var statusText = Get("sc-status");
        if (method is null || path is null || statusText is null || !int.TryParse(statusText, out var status))
        {
            return null;
        }

        if (!string.IsNullOrEmpty(query))
        {
            path = $"{path}?{query}";
        }

        var bytesText = Get("sc-bytes");
        long? bytes = bytesText is not null && long.TryParse(bytesText, out var bytesValue) ? bytesValue : null;

        return new LogEntry(
            timestamp,
            Get("c-ip"),
            method,
            path,
            status,
            Get("cs(User-Agent)") ?? Get("cs-useragent"),
            bytes);
    }

    private static LogEntry? TryParseCloudflareJson(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var timestampText = GetString(root, "EdgeStartTimestamp") ?? GetString(root, "Datetime");
            var method = GetString(root, "ClientRequestMethod");
            var path = GetString(root, "ClientRequestURI");
            var status = GetInt(root, "EdgeResponseStatus");
            if (timestampText is null || method is null || path is null || status is null ||
                !DateTimeOffset.TryParse(timestampText, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var timestamp))
            {
                return null;
            }

            return new LogEntry(
                timestamp.ToUniversalTime(),
                GetString(root, "ClientIP"),
                method,
                path,
                status.Value,
                GetString(root, "ClientRequestUserAgent"),
                GetLong(root, "EdgeResponseBytes"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static LogEntry? TryParseCloudflareCsv(string line, IReadOnlyList<string> fields)
    {
        var values = SplitCsvLine(line);
        if (values.Count != fields.Count)
        {
            return null;
        }

        string? Get(string name)
        {
            for (var i = 0; i < fields.Count; i++)
            {
                if (string.Equals(fields[i].Trim(), name, StringComparison.OrdinalIgnoreCase))
                {
                    return values[i] is "" or "-" ? null : values[i];
                }
            }

            return null;
        }

        var timestampText = Get("EdgeStartTimestamp") ?? Get("Datetime");
        var method = Get("ClientRequestMethod");
        var path = Get("ClientRequestURI");
        var statusText = Get("EdgeResponseStatus");
        if (timestampText is null || method is null || path is null || statusText is null ||
            !int.TryParse(statusText, out var status) ||
            !DateTimeOffset.TryParse(timestampText, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var timestamp))
        {
            return null;
        }

        var bytesText = Get("EdgeResponseBytes");
        long? bytes = bytesText is not null && long.TryParse(bytesText, out var bytesValue) ? bytesValue : null;

        return new LogEntry(
            timestamp.ToUniversalTime(),
            Get("ClientIP"),
            method,
            path,
            status,
            Get("ClientRequestUserAgent"),
            bytes);
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    private static int? GetInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value) ? value : null;

    private static long? GetLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var value) ? value : null;

    private static List<string> SplitW3cLine(string line)
    {
        var values = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var ch in line)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (ch == ' ' && !inQuotes)
            {
                if (current.Length > 0)
                {
                    values.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
        {
            values.Add(current.ToString());
        }

        return values;
    }

    private static List<string> SplitCsvLine(string line)
    {
        var values = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (ch == ',' && !inQuotes)
            {
                values.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        values.Add(current.ToString());
        return values;
    }

    private bool InWindow(DateTimeOffset timestamp) =>
        (_options.WindowStart is null || timestamp >= _options.WindowStart) &&
        (_options.WindowEnd is null || timestamp < _options.WindowEnd);

    private void Reject(LogImportCounters counters, string line)
    {
        counters.LinesRejected++;
        if (counters.ErrorSamples.Count < _options.MaxErrorSamples)
        {
            counters.ErrorSamples.Add(line.Length <= 200 ? line : line[..200]);
        }
    }

    private static async Task<int> ReadAtLeastAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    /// <summary>Replays peeked bytes ahead of the underlying stream.</summary>
    private sealed class PrefixStream : Stream
    {
        private readonly byte[] _prefix;
        private readonly int _prefixLength;
        private readonly Stream _inner;
        private int _position;

        public PrefixStream(byte[] prefix, int prefixLength, Stream inner)
        {
            _prefix = prefix;
            _prefixLength = prefixLength;
            _inner = inner;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var fromPrefix = 0;
            if (_position < _prefixLength)
            {
                fromPrefix = Math.Min(count, _prefixLength - _position);
                Array.Copy(_prefix, _position, buffer, offset, fromPrefix);
                _position += fromPrefix;
            }

            var fromInner = fromPrefix == count ? 0 : _inner.Read(buffer, offset + fromPrefix, count - fromPrefix);
            return fromPrefix + fromInner;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var fromPrefix = 0;
            if (_position < _prefixLength)
            {
                fromPrefix = Math.Min(count, _prefixLength - _position);
                Array.Copy(_prefix, _position, buffer, offset, fromPrefix);
                _position += fromPrefix;
            }

            var fromInner = fromPrefix == count
                ? 0
                : await _inner.ReadAsync(buffer.AsMemory(offset + fromPrefix, count - fromPrefix), cancellationToken);
            return fromPrefix + fromInner;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var fromPrefix = 0;
            if (_position < _prefixLength)
            {
                fromPrefix = Math.Min(buffer.Length, _prefixLength - _position);
                _prefix.AsMemory(_position, fromPrefix).CopyTo(buffer);
                _position += fromPrefix;
            }

            var fromInner = fromPrefix == buffer.Length
                ? 0
                : await _inner.ReadAsync(buffer[fromPrefix..], cancellationToken);
            return fromPrefix + fromInner;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // The caller owns the underlying stream (leaveOpen semantics).
            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// Format auto-detection (FS-CRAWL-002 §11.2): sniffs the first non-empty line
/// of a seekable local stream and restores the position. Manual override is
/// always available through the import request.
/// </summary>
public static class LogFormatDetector
{
    /// <summary>Detects the log format of a local seekable stream.</summary>
    /// <param name="stream">Seekable log stream (position is restored).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The detected format.</returns>
    public static async Task<LogFileFormat> DetectAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        if (!stream.CanSeek)
        {
            return LogFileFormat.ApacheNginxCombined;
        }

        var origin = stream.Position;
        try
        {
            using var reader = new StreamReader(stream, leaveOpen: true);
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("#Software:", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("#Fields:", StringComparison.OrdinalIgnoreCase))
                {
                    return LogFileFormat.IisW3c;
                }

                if (trimmed.StartsWith('{'))
                {
                    return LogFileFormat.CloudflareExport;
                }

                if (trimmed.Contains("ClientIP", StringComparison.OrdinalIgnoreCase) && trimmed.Contains(','))
                {
                    return LogFileFormat.CloudflareExport;
                }

                return LogFileFormat.ApacheNginxCombined;
            }

            return LogFileFormat.ApacheNginxCombined;
        }
        finally
        {
            stream.Position = origin;
        }
    }
}
