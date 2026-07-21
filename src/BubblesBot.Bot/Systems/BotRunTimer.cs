namespace BubblesBot.Bot.Systems;

public readonly record struct BotRunTimerState(
    bool Running, bool Expired, TimeSpan Elapsed, TimeSpan? Remaining);

/// <summary>Process-wide armed-session timer shared by every production mode.</summary>
public sealed class BotRunTimer
{
    private TimeSpan _startedAt = TimeSpan.MinValue;

    public BotRunTimerState Observe(bool armed, int maxMinutes, TimeSpan now)
    {
        if (!armed)
        {
            _startedAt = TimeSpan.MinValue;
            return new(false, false, TimeSpan.Zero,
                maxMinutes > 0 ? TimeSpan.FromMinutes(maxMinutes) : null);
        }

        if (_startedAt == TimeSpan.MinValue) _startedAt = now;
        var elapsed = now - _startedAt;
        if (maxMinutes <= 0)
            return new(true, false, elapsed, null);
        var limit = TimeSpan.FromMinutes(maxMinutes);
        var remaining = limit - elapsed;
        return new(true, remaining <= TimeSpan.Zero, elapsed,
            remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
    }
}
