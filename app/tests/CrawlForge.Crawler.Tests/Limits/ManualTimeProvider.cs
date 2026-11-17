namespace CrawlForge.Crawler.Tests.Limits;

/// <summary>
/// Deterministic test clock: time advances only via <see cref="Advance"/>, and
/// timers created through <see cref="TimeProvider.CreateTimer"/> (including
/// <c>Task.Delay(TimeSpan, TimeProvider, CancellationToken)</c>) fire when the
/// clock passes their due time. Keeps AIMD spacing and cooldown tests fully
/// reproducible (FS-CRAWL-002 §3.4 determinism).
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _now += by;
            due = _timers.Where(t => t.DueAt <= _now).ToList();
            foreach (var timer in due)
            {
                _timers.Remove(timer);
            }
        }

        foreach (var timer in due)
        {
            timer.Fire(_now);
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state, dueTime, period);
        lock (_gate)
        {
            if (dueTime >= TimeSpan.Zero)
            {
                timer.DueAt = _now + dueTime;
                _timers.Add(timer);
            }
        }

        return timer;
    }

    internal void Register(ManualTimer timer, TimeSpan dueTime)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
            if (dueTime >= TimeSpan.Zero)
            {
                timer.DueAt = _now + dueTime;
                _timers.Add(timer);
            }
        }
    }

    internal void Unregister(ManualTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    internal sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private TimeSpan _period;
        private bool _disposed;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
            _period = period;
            DueAt = DateTimeOffset.MaxValue;
            if (dueTime >= TimeSpan.Zero)
            {
                DueAt = owner.GetUtcNow() + dueTime;
            }
        }

        public DateTimeOffset DueAt { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (_disposed)
            {
                return false;
            }

            _period = period;
            _owner.Register(this, dueTime);
            return true;
        }

        public void Dispose()
        {
            _disposed = true;
            _owner.Unregister(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void Fire(DateTimeOffset now)
        {
            if (_disposed)
            {
                return;
            }

            _callback(_state);
            if (_period > TimeSpan.Zero)
            {
                _owner.Register(this, _period);
            }
        }
    }
}
