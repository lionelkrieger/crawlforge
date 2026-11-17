namespace CrawlForge.Api.Tests.Hubs;

/// <summary>
/// Deterministic clock for cadence tests: timers fire only when the test
/// advances time, so throttle/flush/heartbeat assertions never race.
/// </summary>
public sealed class MutableTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<FakeTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    /// <summary>Advances the clock and fires every due timer in order.</summary>
    /// <param name="delta">Time to advance.</param>
    public void Advance(TimeSpan delta)
    {
        lock (_gate)
        {
            _now += delta;
        }

        while (true)
        {
            FakeTimer? due;
            lock (_gate)
            {
                due = _timers
                    .Where(timer => timer.IsArmed && timer.NextDue <= _now)
                    .OrderBy(timer => timer.NextDue)
                    .FirstOrDefault();
                if (due is null)
                {
                    return;
                }

                due.Reschedule(_now);
            }

            due.Fire();
        }
    }

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(callback, state, GetUtcNow() + dueTime, period);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    private sealed class FakeTimer(TimerCallback callback, object? state, DateTimeOffset nextDue, TimeSpan period) : ITimer
    {
        public DateTimeOffset NextDue { get; private set; } = nextDue;

        public bool IsArmed { get; private set; } = true;

        public void Reschedule(DateTimeOffset now)
        {
            NextDue = period > TimeSpan.Zero ? now + period : DateTimeOffset.MaxValue;
            if (period <= TimeSpan.Zero)
            {
                IsArmed = false;
            }
        }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            NextDue = DateTimeOffset.MaxValue; // Tests never re-arm timers.
            IsArmed = dueTime >= TimeSpan.Zero;
            return true;
        }

        public void Dispose() => IsArmed = false;

        public ValueTask DisposeAsync()
        {
            IsArmed = false;
            return ValueTask.CompletedTask;
        }
    }
}
