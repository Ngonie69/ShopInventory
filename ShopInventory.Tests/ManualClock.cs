namespace ShopInventory.Tests;

/// <summary>
/// A <see cref="TimeProvider"/> whose clock — timers included — only moves when a test moves it.
/// </summary>
/// <remarks>
/// For code that debounces or polls, a real clock makes the test a race between the delay and
/// however long the test's own setup took: the assertion is about what landed inside one window,
/// and a machine running the rest of the suite in parallel can stall the setup past it. Nothing
/// scheduled on this clock runs until <see cref="Advance"/> is called, so "everything queued
/// before the window elapsed" becomes a fact about the code rather than about the machine.
///
/// Callbacks are fired on the thread pool rather than on the advancing thread: a timer callback
/// can resume an awaiting continuation inline, and a test that then blocks would be waiting on
/// work it is itself holding up.
/// </remarks>
public sealed class ManualClock : TimeProvider
{
    private readonly object _sync = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_sync) { return _now; }
    }

    /// <summary>Moves the clock on, and fires every timer that has come due.</summary>
    public void Advance(TimeSpan by)
    {
        ManualTimer[] due;

        lock (_sync)
        {
            _now = _now.Add(by);
            due = _timers.Where(t => t.IsDue(_now)).ToArray();
        }

        foreach (var timer in due)
            timer.Fire();
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_sync) { _timers.Add(timer); }
        timer.Change(dueTime, period);
        return timer;
    }

    private void Remove(ManualTimer timer)
    {
        lock (_sync) { _timers.Remove(timer); }
    }

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        private readonly object _sync = new();
        private DateTimeOffset? _dueAt;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            // Read the clock before taking this timer's lock: Advance holds the clock's lock while
            // it asks each timer whether it is due, so taking them the other way round here would
            // be the two threads deadlocking each other.
            var now = clock.GetUtcNow();

            lock (_sync)
            {
                _dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : now.Add(dueTime);
                _period = period;
            }

            return true;
        }

        public bool IsDue(DateTimeOffset now)
        {
            lock (_sync) { return _dueAt is { } dueAt && now >= dueAt; }
        }

        /// <summary>Re-arms (or disarms) the timer, then runs the callback off the advancing thread.</summary>
        public void Fire()
        {
            var now = clock.GetUtcNow();

            lock (_sync)
            {
                _dueAt = _period == Timeout.InfiniteTimeSpan || _period == TimeSpan.Zero
                    ? null
                    : now.Add(_period);
            }

            ThreadPool.UnsafeQueueUserWorkItem(_ => callback(state), null);
        }

        public void Dispose() => clock.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
