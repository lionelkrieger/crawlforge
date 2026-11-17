using CrawlForge.Security.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CrawlForge.Host.Composition;

/// <summary>
/// Logging bridge (WP-06-01 reopen): forwards framework log events into the
/// accepted <see cref="LocalStructuredLogSink"/> so every subsystem log lands
/// in the user-viewable local JSON-line files (FS-CRAWL-001 §10.5). The sink
/// owns the privacy invariant — caller properties pass its identity-dimension
/// filter and no ambient user/machine/account context is ever added here
/// (exclusion row 6). Registered as an <see cref="ILoggerProvider"/> singleton;
/// the default logging builder consumes it alongside the console provider, so
/// existing log behavior is preserved unchanged.
/// </summary>
public sealed class LocalStructuredLogProvider : ILoggerProvider
{
    private readonly LocalStructuredLogSink _sink;

    /// <summary>Creates the provider over the shared local sink.</summary>
    /// <param name="sink">Process-wide structured log sink.</param>
    public LocalStructuredLogProvider(LocalStructuredLogSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new LocalStructuredLogger(categoryName, _sink);

    /// <inheritdoc />
    public void Dispose()
    {
        // The sink is a DI-owned singleton; its lifetime is the container's.
    }
}

/// <summary>
/// One category logger writing through the local structured sink. Only
/// technical dimensions are attached (category, event id, exception type and
/// message); the sink's identity filter is the final guard.
/// </summary>
internal sealed class LocalStructuredLogger : ILogger
{
    private readonly string _category;
    private readonly LocalStructuredLogSink _sink;

    public LocalStructuredLogger(string category, LocalStructuredLogSink sink)
    {
        _category = category;
        _sink = sink;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel) || formatter is null)
        {
            return;
        }

        var properties = new Dictionary<string, object?>
        {
            ["category"] = _category
        };
        if (eventId.Id != 0)
        {
            properties["eventId"] = eventId.Id;
        }

        // Forward caller properties that are safe technical scalars; the
        // sink's identity-dimension filter is the authoritative identity guard
        // (exclusion row 6). Framework state can carry non-serializable
        // objects (reflection metadata, request features) — those never enter
        // the JSON-line payload.
        if (state is IEnumerable<KeyValuePair<string, object?>> stateProperties)
        {
            foreach (var pair in stateProperties)
            {
                if (pair.Key != "{OriginalFormat}" && IsLoggableValue(pair.Value))
                {
                    properties[pair.Key] = pair.Value;
                }
            }
        }

        if (exception is not null)
        {
            properties["exceptionType"] = exception.GetType().Name;
            properties["exceptionMessage"] = exception.Message;
        }

        _sink.Write(MapLevel(logLevel), formatter(state, exception), properties);
    }

    private static bool IsLoggableValue(object? value) =>
        value is null or string or bool or byte or sbyte or short or ushort or int or uint
            or long or ulong or float or double or decimal or DateTime or DateTimeOffset
            or TimeSpan or Guid or Enum;

    private static LocalLogLevel MapLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => LocalLogLevel.Trace,
        LogLevel.Debug => LocalLogLevel.Debug,
        LogLevel.Information => LocalLogLevel.Information,
        LogLevel.Warning => LocalLogLevel.Warning,
        LogLevel.Error => LocalLogLevel.Error,
        LogLevel.Critical => LocalLogLevel.Critical,
        _ => LocalLogLevel.Information
    };

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
