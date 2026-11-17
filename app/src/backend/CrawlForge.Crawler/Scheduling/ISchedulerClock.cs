namespace CrawlForge.Crawler.Scheduling;

/// <summary>
/// Injectable scheduler clock (FS-CRAWL-001 §6.2): all recurrence evaluation and
/// catch-up classification are deterministic against this clock. Tests substitute a
/// controllable clock; production uses <see cref="SystemSchedulerClock"/>.
/// </summary>
public interface ISchedulerClock
{
    /// <summary>Current instant in UTC.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>The user's local timezone used to evaluate calendar-style recurrence rules.</summary>
    TimeZoneInfo LocalTimeZone { get; }
}

/// <summary>
/// Production clock: system UTC time and the machine's configured local timezone.
/// The timezone is a user locale setting, not an identity dimension.
/// </summary>
public sealed class SystemSchedulerClock : ISchedulerClock
{
    /// <inheritdoc/>
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    /// <inheritdoc/>
    public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;
}
