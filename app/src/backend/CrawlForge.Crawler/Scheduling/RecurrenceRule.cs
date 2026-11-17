using System.Globalization;

namespace CrawlForge.Crawler.Scheduling;

/// <summary>Recurrence rule families (FS-CRAWL-001 §6.1).</summary>
public enum RecurrenceKind
{
    /// <summary>Fixed interval between firings.</summary>
    Interval = 0,

    /// <summary>Calendar-style rule: local time of day plus day-of-week or day-of-month selectors.</summary>
    Calendar = 1,
}

/// <summary>
/// Deterministic recurrence rule (FS-CRAWL-001 §6.1): either a fixed interval or a
/// calendar-style rule evaluated in the user's local timezone. The expression grammar
/// (stored in <c>schedule_definitions.recurrence_rule</c>, invariant culture):
/// <list type="bullet">
/// <item><c>interval:&lt;timespan&gt;</c> — constant-format TimeSpan, e.g. <c>interval:00:30:00</c> or <c>interval:1.00:00:00</c>.</item>
/// <item><c>calendar:HH:mm</c> — daily at the given local time.</item>
/// <item><c>calendar:HH:mm;days=Mon,Wed,Fri</c> — selected weekdays (three-letter English names).</item>
/// <item><c>calendar:HH:mm;days=1,15</c> — selected days of the month (1–31).</item>
/// </list>
/// Weekday and day-of-month selectors must not be mixed. Evaluation never consults
/// any remote service; the next occurrence is strictly after the supplied instant.
/// </summary>
public sealed class RecurrenceRule
{
    private const int MaxSearchDays = 400;

    private static readonly Dictionary<string, DayOfWeek> WeekdayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Mon"] = DayOfWeek.Monday,
        ["Tue"] = DayOfWeek.Tuesday,
        ["Wed"] = DayOfWeek.Wednesday,
        ["Thu"] = DayOfWeek.Thursday,
        ["Fri"] = DayOfWeek.Friday,
        ["Sat"] = DayOfWeek.Saturday,
        ["Sun"] = DayOfWeek.Sunday,
    };

    private RecurrenceRule(RecurrenceKind kind, TimeSpan interval, TimeOnly timeOfDay, IReadOnlyList<DayOfWeek>? weekdays, IReadOnlyList<int>? monthDays, string expression)
    {
        Kind = kind;
        Interval = interval;
        TimeOfDay = timeOfDay;
        Weekdays = weekdays;
        MonthDays = monthDays;
        Expression = expression;
    }

    /// <summary>Rule family.</summary>
    public RecurrenceKind Kind { get; }

    /// <summary>Fixed interval (interval rules only).</summary>
    public TimeSpan Interval { get; }

    /// <summary>Local time of day (calendar rules only).</summary>
    public TimeOnly TimeOfDay { get; }

    /// <summary>Selected weekdays, or null when the rule is daily or day-of-month based.</summary>
    public IReadOnlyList<DayOfWeek>? Weekdays { get; }

    /// <summary>Selected days of the month, or null when the rule is daily or weekday based.</summary>
    public IReadOnlyList<int>? MonthDays { get; }

    /// <summary>The canonical rule expression.</summary>
    public string Expression { get; }

    /// <summary>Parses a rule expression.</summary>
    /// <param name="expression">Rule expression in the documented grammar.</param>
    /// <returns>The parsed rule.</returns>
    /// <exception cref="FormatException">The expression is malformed.</exception>
    public static RecurrenceRule Parse(string expression)
    {
        if (!TryParse(expression, out var rule, out var error))
        {
            throw new FormatException(error);
        }

        return rule;
    }

    /// <summary>Attempts to parse a rule expression.</summary>
    /// <param name="expression">Rule expression in the documented grammar.</param>
    /// <param name="rule">The parsed rule when successful.</param>
    /// <param name="error">Failure detail when unsuccessful.</param>
    /// <returns>True when the expression is valid.</returns>
    public static bool TryParse(string? expression, out RecurrenceRule rule, out string? error)
    {
        rule = null!;
        error = null;

        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "The recurrence rule must not be empty.";
            return false;
        }

        var separator = expression.IndexOf(':');
        if (separator <= 0 || separator == expression.Length - 1)
        {
            error = "The recurrence rule must start with 'interval:' or 'calendar:' followed by a value.";
            return false;
        }

        var scheme = expression[..separator];
        var value = expression[(separator + 1)..];

        if (string.Equals(scheme, "interval", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseInterval(value, expression, out rule, out error);
        }

        if (string.Equals(scheme, "calendar", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseCalendar(value, expression, out rule, out error);
        }

        error = $"Unknown recurrence scheme '{scheme}'; expected 'interval' or 'calendar'.";
        return false;
    }

    /// <summary>
    /// Computes the next occurrence strictly after the supplied instant. Interval rules
    /// add the fixed duration; calendar rules search local dates in the supplied timezone.
    /// Daylight-saving handling is deterministic: an ambiguous local time resolves to the
    /// standard-time offset, and a local time that does not exist (spring forward) skips
    /// to the next matching date.
    /// </summary>
    /// <param name="afterUtc">The instant the next occurrence must follow (UTC).</param>
    /// <param name="localTimeZone">The user's local timezone for calendar evaluation.</param>
    /// <returns>The next occurrence in UTC.</returns>
    /// <exception cref="InvalidOperationException">No occurrence exists within the bounded search window.</exception>
    public DateTimeOffset GetNextOccurrence(DateTimeOffset afterUtc, TimeZoneInfo localTimeZone)
    {
        ArgumentNullException.ThrowIfNull(localTimeZone);
        var after = afterUtc.ToUniversalTime();

        if (Kind == RecurrenceKind.Interval)
        {
            return after + Interval;
        }

        var localAfter = TimeZoneInfo.ConvertTime(after, localTimeZone);
        var date = localAfter.Date;

        for (var day = 0; day < MaxSearchDays; day++, date = date.AddDays(1))
        {
            if (!MatchesDate(date))
            {
                continue;
            }

            var candidateLocal = date.Add(TimeOfDay.ToTimeSpan());
            if (localTimeZone.IsInvalidTime(candidateLocal))
            {
                continue;
            }

            DateTimeOffset candidateUtc;
            if (localTimeZone.IsAmbiguousTime(candidateLocal))
            {
                // Deterministic fall-back resolution: the standard-time (smaller) offset.
                var standardOffset = localTimeZone.GetAmbiguousTimeOffsets(candidateLocal).Min();
                candidateUtc = new DateTimeOffset(candidateLocal, standardOffset).ToUniversalTime();
            }
            else
            {
                candidateUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(candidateLocal, localTimeZone), TimeSpan.Zero);
            }

            if (candidateUtc > after)
            {
                return candidateUtc;
            }
        }

        throw new InvalidOperationException($"The recurrence rule '{Expression}' produced no occurrence within {MaxSearchDays} days.");
    }

    /// <summary>Returns the canonical rule expression.</summary>
    /// <returns>The expression this rule was parsed from.</returns>
    public override string ToString() => Expression;

    private bool MatchesDate(DateTime date)
    {
        if (Weekdays is not null)
        {
            return Weekdays.Contains(date.DayOfWeek);
        }

        if (MonthDays is not null)
        {
            return MonthDays.Contains(date.Day);
        }

        return true;
    }

    private static bool TryParseInterval(string value, string expression, out RecurrenceRule rule, out string? error)
    {
        rule = null!;
        error = null;

        if (!TimeSpan.TryParseExact(value.Trim(), "c", CultureInfo.InvariantCulture, TimeSpanStyles.None, out var interval))
        {
            error = $"Invalid interval '{value}'; expected a constant-format TimeSpan such as '00:30:00' or '1.00:00:00'.";
            return false;
        }

        if (interval <= TimeSpan.Zero || interval > TimeSpan.FromDays(366))
        {
            error = "The interval must be positive and at most 366 days.";
            return false;
        }

        rule = new RecurrenceRule(RecurrenceKind.Interval, interval, default, null, null, expression);
        return true;
    }

    private static bool TryParseCalendar(string value, string expression, out RecurrenceRule rule, out string? error)
    {
        rule = null!;
        error = null;

        var parts = value.Split(';', 2);
        if (!TimeOnly.TryParseExact(parts[0].Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var timeOfDay))
        {
            error = $"Invalid calendar time '{parts[0]}'; expected 'HH:mm' (24-hour local time).";
            return false;
        }

        IReadOnlyList<DayOfWeek>? weekdays = null;
        IReadOnlyList<int>? monthDays = null;

        if (parts.Length == 2)
        {
            var selector = parts[1].Trim();
            const string daysPrefix = "days=";
            if (!selector.StartsWith(daysPrefix, StringComparison.OrdinalIgnoreCase))
            {
                error = $"Unknown calendar selector '{selector}'; expected 'days=...'.";
                return false;
            }

            var tokens = selector[daysPrefix.Length..].Split(',', StringSplitOptions.TrimEntries);
            if (tokens.Length == 0 || tokens.Any(static t => t.Length == 0))
            {
                error = "The day selector must list at least one day.";
                return false;
            }

            var parsedWeekdays = new List<DayOfWeek>();
            var parsedMonthDays = new List<int>();
            foreach (var token in tokens)
            {
                if (WeekdayNames.TryGetValue(token, out var weekday))
                {
                    parsedWeekdays.Add(weekday);
                }
                else if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var monthDay) && monthDay is >= 1 and <= 31)
                {
                    parsedMonthDays.Add(monthDay);
                }
                else
                {
                    error = $"Invalid day selector '{token}'; expected Mon–Sun names or month days 1–31.";
                    return false;
                }
            }

            if (parsedWeekdays.Count > 0 && parsedMonthDays.Count > 0)
            {
                error = "Weekday and day-of-month selectors must not be mixed.";
                return false;
            }

            if (parsedWeekdays.Count > 0)
            {
                if (parsedWeekdays.Distinct().Count() != parsedWeekdays.Count)
                {
                    error = "The day selector contains duplicate weekdays.";
                    return false;
                }

                weekdays = parsedWeekdays;
            }
            else
            {
                if (parsedMonthDays.Distinct().Count() != parsedMonthDays.Count)
                {
                    error = "The day selector contains duplicate month days.";
                    return false;
                }

                monthDays = parsedMonthDays;
            }
        }

        rule = new RecurrenceRule(RecurrenceKind.Calendar, default, timeOfDay, weekdays, monthDays, expression);
        return true;
    }
}
