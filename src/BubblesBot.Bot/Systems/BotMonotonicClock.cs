namespace BubblesBot.Bot.Systems;

public static class BotMonotonicClock
{
    private static readonly object Gate = new();
    private static long _pausedAt;
    private static long _accumulatedPausedTicks;

    /// <summary>Raw wall-monotonic time. Runtime limits use this so loading/alt-tab time counts.</summary>
    public static TimeSpan RawNow => FromTicks(System.Diagnostics.Stopwatch.GetTimestamp());

    /// <summary>
    /// Automation-active monotonic time. It freezes while the input gate is closed so behavior
    /// phase/click timeouts do not expire merely because the bot was disarmed or unable to act.
    /// </summary>
    public static TimeSpan Now
    {
        get
        {
            lock (Gate)
            {
                var raw = System.Diagnostics.Stopwatch.GetTimestamp();
                var pausedNow = _pausedAt == 0 ? 0 : raw - _pausedAt;
                return FromTicks(raw - _accumulatedPausedTicks - pausedNow);
            }
        }
    }

    public static void SetPaused(bool paused)
    {
        lock (Gate)
        {
            var raw = System.Diagnostics.Stopwatch.GetTimestamp();
            if (paused)
            {
                if (_pausedAt == 0) _pausedAt = raw;
                return;
            }

            if (_pausedAt == 0) return;
            _accumulatedPausedTicks += raw - _pausedAt;
            _pausedAt = 0;
        }
    }

    /// <summary>
    /// Returns elapsed monotonic time without overflowing when <see cref="TimeSpan.MinValue"/>
    /// is used as the conventional "never happened" sentinel. A never-observed timestamp is
    /// treated as infinitely old so first-use cooldown and log-throttle checks are immediately
    /// eligible.
    /// </summary>
    public static TimeSpan ElapsedSince(TimeSpan timestamp)
    {
        if (timestamp == TimeSpan.MinValue) return TimeSpan.MaxValue;
        var now = Now;
        return timestamp >= now ? TimeSpan.Zero : now - timestamp;
    }

    private static TimeSpan FromTicks(long timestamp)
        => TimeSpan.FromSeconds(timestamp / (double)System.Diagnostics.Stopwatch.Frequency);
}
