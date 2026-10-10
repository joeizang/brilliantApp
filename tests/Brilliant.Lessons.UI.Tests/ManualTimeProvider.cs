namespace Brilliant.Lessons.UI.Tests;

/// <summary>A clock whose timers fire only when a test says so, so playback can be walked one tick at a time.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    public List<ManualTimer> Timers { get; } = [];

    /// <summary>The timers that have been created and not disposed.</summary>
    public IEnumerable<ManualTimer> Live => Timers.Where(t => !t.Disposed);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(callback, state, period);
        Timers.Add(timer);
        return timer;
    }

    /// <summary>Fires every live timer once.</summary>
    public void Tick()
    {
        foreach (var timer in Live.ToList()) timer.Fire();
    }

    /// <summary>Fires every timer ever created, disposed or not: a tick that was already on its way when its timer was stopped.</summary>
    public void TickEverything()
    {
        foreach (var timer in Timers.ToList()) timer.Fire();
    }

    public sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan period) : ITimer
    {
        public TimeSpan Period { get; private set; } = period;
        public bool Disposed { get; private set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Period = period;
            return true;
        }

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
