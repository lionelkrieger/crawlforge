using CrawlForge.Crawler.Scheduling;
using Xunit;

namespace CrawlForge.Crawler.Tests.Scheduling;

/// <summary>
/// Deterministic recurrence evaluation (FS-CRAWL-001 §6.1): fixed intervals and
/// calendar rules in the user's local timezone, including daylight-saving edges.
/// All assertions run against fixed instants — no real-time waiting.
/// </summary>
public sealed class RecurrenceRuleTests
{
    private static readonly DateTimeOffset Saturday = new(2026, 11, 14, 8, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "Interval rule adds the fixed duration")]
    public void IntervalAddsDuration()
    {
        var rule = RecurrenceRule.Parse("interval:00:30:00");

        Assert.Equal(RecurrenceKind.Interval, rule.Kind);
        Assert.Equal(TimeSpan.FromMinutes(30), rule.Interval);
        Assert.Equal(Saturday.AddMinutes(30), rule.GetNextOccurrence(Saturday, TimeZoneInfo.Utc));
    }

    [Fact(DisplayName = "Interval rule parses day-qualified constant format")]
    public void IntervalParsesDayQualifiedFormat()
    {
        var rule = RecurrenceRule.Parse("interval:1.00:00:00");

        Assert.Equal(TimeSpan.FromDays(1), rule.Interval);
        Assert.Equal(Saturday.AddDays(1), rule.GetNextOccurrence(Saturday, TimeZoneInfo.Utc));
    }

    [Fact(DisplayName = "Interval occurrence is strictly after the supplied instant")]
    public void IntervalIsStrictlyAfter()
    {
        var rule = RecurrenceRule.Parse("interval:01:00:00");
        var due = Saturday.AddHours(1);

        var next = rule.GetNextOccurrence(due, TimeZoneInfo.Utc);

        Assert.True(next > due);
        Assert.Equal(due.AddHours(1), next);
    }

    [Fact(DisplayName = "Daily calendar rule rolls to the next day when the time has passed")]
    public void DailyCalendarRollsToNextDay()
    {
        var rule = RecurrenceRule.Parse("calendar:07:30");

        var next = rule.GetNextOccurrence(Saturday, TimeZoneInfo.Utc);

        Assert.Equal(new DateTimeOffset(2026, 11, 15, 7, 30, 0, TimeSpan.Zero), next);
    }

    [Fact(DisplayName = "Daily calendar rule fires same day when the time is ahead")]
    public void DailyCalendarFiresSameDay()
    {
        var rule = RecurrenceRule.Parse("calendar:07:30");
        var earlyMorning = new DateTimeOffset(2026, 11, 14, 6, 0, 0, TimeSpan.Zero);

        var next = rule.GetNextOccurrence(earlyMorning, TimeZoneInfo.Utc);

        Assert.Equal(new DateTimeOffset(2026, 11, 14, 7, 30, 0, TimeSpan.Zero), next);
    }

    [Fact(DisplayName = "Weekday selector skips to the next selected day")]
    public void WeekdaySelectorSkipsToNextSelectedDay()
    {
        // 2026-11-14 is a Saturday; the next Mon/Wed/Fri is Monday 2026-11-16.
        var rule = RecurrenceRule.Parse("calendar:07:30;days=Mon,Wed,Fri");

        var next = rule.GetNextOccurrence(Saturday, TimeZoneInfo.Utc);

        Assert.Equal(new DateTimeOffset(2026, 11, 16, 7, 30, 0, TimeSpan.Zero), next);
        Assert.Equal(DayOfWeek.Monday, next.DayOfWeek);
    }

    [Fact(DisplayName = "Day-of-month selector picks the next listed day")]
    public void MonthDaySelectorPicksNextListedDay()
    {
        var rule = RecurrenceRule.Parse("calendar:09:00;days=1,15");

        var next = rule.GetNextOccurrence(Saturday, TimeZoneInfo.Utc);

        Assert.Equal(new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero), next);
    }

    [Fact(DisplayName = "Day-of-month 31 skips months without a 31st")]
    public void MonthDay31SkipsShortMonths()
    {
        var rule = RecurrenceRule.Parse("calendar:09:00;days=31");

        var next = rule.GetNextOccurrence(Saturday, TimeZoneInfo.Utc);

        Assert.Equal(new DateTimeOffset(2026, 12, 31, 9, 0, 0, TimeSpan.Zero), next);
    }

    [Fact(DisplayName = "Calendar rule evaluates in the user's local timezone")]
    public void CalendarEvaluatesInLocalTimezone()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var rule = RecurrenceRule.Parse("calendar:07:30");

        // 06:30 UTC is 07:30 local (CET, UTC+1): the same-day slot is not strictly after.
        var after = new DateTimeOffset(2026, 11, 14, 6, 30, 0, TimeSpan.Zero);
        var next = rule.GetNextOccurrence(after, berlin);

        Assert.Equal(new DateTimeOffset(2026, 11, 15, 6, 30, 0, TimeSpan.Zero), next);
    }

    [Fact(DisplayName = "Non-UTC input instants are normalized before evaluation")]
    public void NonUtcInputIsNormalized()
    {
        var rule = RecurrenceRule.Parse("calendar:07:30");
        var afterWithOffset = new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.FromHours(2));

        var next = rule.GetNextOccurrence(afterWithOffset, TimeZoneInfo.Utc);

        Assert.Equal(new DateTimeOffset(2026, 11, 15, 7, 30, 0, TimeSpan.Zero), next);
        Assert.Equal(TimeSpan.Zero, next.Offset);
    }

    [Fact(DisplayName = "Spring-forward: a nonexistent local time skips to the next matching day")]
    public void SpringForwardSkipsInvalidLocalTime()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var rule = RecurrenceRule.Parse("calendar:02:30");

        // 2026-03-29 02:00 → 03:00 in Berlin: 02:30 does not exist that day.
        var after = new DateTimeOffset(2026, 3, 28, 1, 30, 0, TimeSpan.Zero);
        var next = rule.GetNextOccurrence(after, berlin);

        Assert.Equal(new DateTimeOffset(2026, 3, 30, 0, 30, 0, TimeSpan.Zero), next);
    }

    [Fact(DisplayName = "Fall-back: an ambiguous local time resolves to the standard-time offset")]
    public void FallBackAmbiguousTimeUsesStandardOffset()
    {
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var rule = RecurrenceRule.Parse("calendar:02:30");

        // 2026-10-25 02:30 occurs twice; standard time (UTC+1) is the deterministic pick.
        var after = new DateTimeOffset(2026, 10, 24, 0, 30, 0, TimeSpan.Zero);
        var next = rule.GetNextOccurrence(after, berlin);

        Assert.Equal(new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero), next);
    }

    [Fact(DisplayName = "Scheme and weekday names are case-insensitive")]
    public void ParsingIsCaseInsensitive()
    {
        var interval = RecurrenceRule.Parse("INTERVAL:00:30:00");
        Assert.Equal(TimeSpan.FromMinutes(30), interval.Interval);

        var calendar = RecurrenceRule.Parse("calendar:07:30;days=mon,WED");
        Assert.NotNull(calendar.Weekdays);
        Assert.Contains(DayOfWeek.Monday, calendar.Weekdays);
        Assert.Contains(DayOfWeek.Wednesday, calendar.Weekdays);
    }

    [Theory(DisplayName = "Malformed rules are rejected")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("weekly:Mon")]
    [InlineData("interval")]
    [InlineData("interval:")]
    [InlineData("interval:00:00:00")]
    [InlineData("interval:-01:00:00")]
    [InlineData("interval:400.00:00:00")]
    [InlineData("interval:not-a-timespan")]
    [InlineData("calendar:25:00")]
    [InlineData("calendar:07:60")]
    [InlineData("calendar:07:30;days=")]
    [InlineData("calendar:07:30;days=Mon,1")]
    [InlineData("calendar:07:30;days=Xyz")]
    [InlineData("calendar:07:30;days=0")]
    [InlineData("calendar:07:30;days=32")]
    [InlineData("calendar:07:30;days=Mon,Mon")]
    [InlineData("calendar:07:30;days=1,1")]
    [InlineData("calendar:07:30;months=1")]
    public void MalformedRulesAreRejected(string expression)
    {
        Assert.False(RecurrenceRule.TryParse(expression, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Throws<FormatException>(() => RecurrenceRule.Parse(expression));
    }

    [Fact(DisplayName = "TryParse succeeds for valid rules and reports the canonical expression")]
    public void TryParseSucceedsForValidRules()
    {
        Assert.True(RecurrenceRule.TryParse("calendar:07:30;days=1,15", out var rule, out var error));
        Assert.Null(error);
        Assert.Equal("calendar:07:30;days=1,15", rule.ToString());
        Assert.Equal([1, 15], rule.MonthDays);
    }
}
