namespace Atelia.Galatea.Server.Tests;

/// <summary>Timer-capable synthetic clock. Periodic ticks coalesce after a jump.</summary>
internal sealed class GalateaLabClock : TimeProvider {
    private readonly object _gate = new();
    private readonly List<ClockTimer> _timers = [];
    private long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (_gate) { return _ticks; } }
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(GetTimestamp());
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_gate) {
            var timer = new ClockTimer(this, callback, state);
            timer.Change(dueTime, period);
            _timers.Add(timer);
            return timer;
        }
    }
    internal void Advance(TimeSpan duration) {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        ClockTimer[] due;
        lock (_gate) {
            _ticks = checked(_ticks + duration.Ticks);
            due = _timers.Where(timer => !timer.Disposed && timer.Due <= _ticks).ToArray();
            foreach (ClockTimer timer in due) {
                timer.Due = timer.Period > TimeSpan.Zero
                    ? checked(_ticks + timer.Period.Ticks) : long.MaxValue;
            }
        }
        foreach (ClockTimer timer in due) { timer.Callback(timer.State); }
    }
    private sealed class ClockTimer(GalateaLabClock clock, TimerCallback callback, object? state) : ITimer {
        internal TimerCallback Callback => callback;
        internal object? State => state;
        internal long Due;
        internal TimeSpan Period;
        internal bool Disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period) {
            if (dueTime < TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan) {
                throw new ArgumentOutOfRangeException(nameof(dueTime));
            }
            if (period < TimeSpan.Zero && period != Timeout.InfiniteTimeSpan) {
                throw new ArgumentOutOfRangeException(nameof(period));
            }
            lock (clock._gate) {
                if (Disposed) { return false; }
                Due = dueTime == Timeout.InfiniteTimeSpan
                    ? long.MaxValue : checked(clock._ticks + dueTime.Ticks);
                Period = period;
                return true;
            }
        }
        public void Dispose() { lock (clock._gate) { Disposed = true; clock._timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
