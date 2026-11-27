namespace Chaos.Tests;

sealed class ManualClock : TimeProvider
{
    readonly object gate = new();
    readonly List<ManualTimer> timers = [];
    DateTimeOffset now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
            return now;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan span)
    {
        DateTimeOffset end;
        lock (gate)
            end = now + span;
        while (true)
        {
            ManualTimer? next;
            lock (gate)
            {
                next = timers.Where(timer => timer.Due <= end).MinBy(timer => timer.Due);
                if (next is null)
                {
                    now = end;
                    return;
                }
                now = next.Due;
                timers.Remove(next);
            }
            // Outside the lock: a callback may start a timer.
            next.Fire();
        }
    }

    public TimeSpan? NextDue(TimeSpan span)
    {
        lock (gate)
            return timers.Where(timer => timer.Due - now <= span).MinBy(timer => timer.Due) is { } first ? first.Due - now : null;
    }

    sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset Due { get; private set; }

        // Timers here fire once.
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock.gate)
            {
                clock.timers.Remove(this);
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    Due = clock.now + dueTime;
                    clock.timers.Add(this);
                }
            }
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (clock.gate)
                clock.timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
