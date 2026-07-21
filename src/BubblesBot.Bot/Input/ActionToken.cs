namespace BubblesBot.Bot.Input;

using BubblesBot.Bot.Systems;

public enum ActionOutcome { Pending, Confirmed, SettledUnverified, TimedOut, Cancelled }

/// <summary>Lifecycle receipt for one accepted edge action.</summary>
public sealed class ActionToken
{
    private readonly Func<bool>? _confirmedPredicate;

    public ActionToken(
        long actionId,
        string description,
        DateTime requestedAt,
        int timeoutMs,
        Func<bool>? confirmedPredicate,
        TimeSpan? requestedMonotonic = null)
    {
        ActionId = actionId;
        Description = description;
        RequestedAt = requestedAt;
        RequestedMonotonic = requestedMonotonic ?? BotMonotonicClock.Now;
        TimeoutMs = timeoutMs;
        _confirmedPredicate = confirmedPredicate;
    }

    public long ActionId { get; }
    public string Description { get; }
    public DateTime RequestedAt { get; }
    public TimeSpan RequestedMonotonic { get; }
    public int TimeoutMs { get; }
    public DateTime? DispatchedAt { get; private set; }
    public DateTime? ConfirmedAt { get; private set; }
    public DateTime? ResolvedAt { get; private set; }
    public TimeSpan? DispatchedMonotonic { get; private set; }
    public TimeSpan? ResolvedMonotonic { get; private set; }
    public ActionOutcome Outcome { get; private set; }
    public bool IsResolved => Outcome is not ActionOutcome.Pending;

    public void MarkDispatched(DateTime now, TimeSpan? monotonicNow = null)
    {
        DispatchedAt ??= now;
        DispatchedMonotonic ??= monotonicNow ?? BotMonotonicClock.Now;
    }

    public bool Poll(DateTime now, TimeSpan? monotonicNow = null)
    {
        if (IsResolved) return true;
        if (DispatchedAt is null) return false;
        var monotonic = monotonicNow ?? BotMonotonicClock.Now;

        if (_confirmedPredicate is not null && _confirmedPredicate())
        {
            ConfirmedAt = now;
            ResolvedAt = now;
            ResolvedMonotonic = monotonic;
            Outcome = ActionOutcome.Confirmed;
            return true;
        }

        if (DispatchedMonotonic is { } dispatched &&
            (monotonic - dispatched).TotalMilliseconds >= TimeoutMs)
        {
            ResolvedAt = now;
            ResolvedMonotonic = monotonic;
            Outcome = _confirmedPredicate is null
                ? ActionOutcome.SettledUnverified
                : ActionOutcome.TimedOut;
            return true;
        }
        return false;
    }

    public void ConfirmImmediately(DateTime now, TimeSpan? monotonicNow = null)
    {
        var monotonic = monotonicNow ?? BotMonotonicClock.Now;
        MarkDispatched(now, monotonic);
        ConfirmedAt = now;
        ResolvedAt = now;
        ResolvedMonotonic = monotonic;
        Outcome = ActionOutcome.Confirmed;
    }

    public void Cancel(DateTime now, TimeSpan? monotonicNow = null)
    {
        if (IsResolved) return;
        ResolvedAt = now;
        ResolvedMonotonic = monotonicNow ?? BotMonotonicClock.Now;
        Outcome = ActionOutcome.Cancelled;
    }
}
