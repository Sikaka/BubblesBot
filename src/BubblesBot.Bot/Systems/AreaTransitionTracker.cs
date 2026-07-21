using BubblesBot.Bot.Behaviors;
using BubblesBot.Core.Game;

namespace BubblesBot.Bot.Systems;

// SafeHub = hideout/town; Map = a fresh map instance; SubArea = a detached same-map region
// (Harvest grove, side area) reached by an in-map transition; BossArena = a boss room reached
// by an in-map transition. SubArea/BossArena keep the parent run ledger (see SubAreaTracker).
public enum AreaRole { Unknown, SafeHub, Map, SubArea, BossArena }
public enum AreaTransitionOutcome { Idle, WaitingForChange, VerifyingDestination, Confirmed, UnexpectedDestination, TimedOut }

public sealed record AreaTransitionState(
    string IntentId,
    uint OriginAreaHash,
    AreaRole OriginRole,
    AreaRole ExpectedDestination,
    uint ObservedAreaHash,
    AreaRole ObservedRole,
    AreaTransitionOutcome Outcome,
    double ElapsedMs);

/// <summary>Pure, deterministic verifier for one intended area transition.</summary>
public sealed class AreaTransitionTracker
{
    private readonly TimeSpan _destinationEvidenceTimeout;
    private TimeSpan _startedAt;
    private TimeSpan? _areaChangedAt;

    public AreaTransitionTracker(TimeSpan? destinationEvidenceTimeout = null)
        => _destinationEvidenceTimeout = destinationEvidenceTimeout ?? TimeSpan.FromSeconds(5);

    public AreaTransitionState State { get; private set; } =
        new("", 0, AreaRole.Unknown, AreaRole.Unknown, 0, AreaRole.Unknown, AreaTransitionOutcome.Idle, 0);

    public void Start(uint originAreaHash, AreaRole originRole, AreaRole expectedDestination, TimeSpan now)
    {
        _startedAt = now;
        _areaChangedAt = null;
        State = new(
            Guid.NewGuid().ToString("N"), originAreaHash, originRole, expectedDestination,
            0, AreaRole.Unknown, AreaTransitionOutcome.WaitingForChange, 0);
    }

    public AreaTransitionState Observe(uint areaHash, AreaRole role, TimeSpan now)
    {
        if (State.Outcome is AreaTransitionOutcome.Idle
            or AreaTransitionOutcome.Confirmed
            or AreaTransitionOutcome.UnexpectedDestination
            or AreaTransitionOutcome.TimedOut)
            return State;

        if (areaHash == 0 || areaHash == State.OriginAreaHash)
            return State = State with { ElapsedMs = (now - _startedAt).TotalMilliseconds };

        _areaChangedAt ??= now;
        var outcome = role == State.ExpectedDestination
            ? AreaTransitionOutcome.Confirmed
            : role != AreaRole.Unknown
                ? AreaTransitionOutcome.UnexpectedDestination
                : now - _areaChangedAt.Value >= _destinationEvidenceTimeout
                    ? AreaTransitionOutcome.TimedOut
                    : AreaTransitionOutcome.VerifyingDestination;

        return State = State with
        {
            ObservedAreaHash = areaHash,
            ObservedRole = role,
            Outcome = outcome,
            ElapsedMs = (now - _startedAt).TotalMilliseconds,
        };
    }

    public void Reset()
    {
        _areaChangedAt = null;
        State = new("", 0, AreaRole.Unknown, AreaRole.Unknown, 0, AreaRole.Unknown, AreaTransitionOutcome.Idle, 0);
    }

    public static TimeSpan MonotonicNow()
        => TimeSpan.FromSeconds(System.Diagnostics.Stopwatch.GetTimestamp() /
                                (double)System.Diagnostics.Stopwatch.Frequency);
}

public static class WorldAreaClassifier
{
    public static AreaRole Classify(BehaviorContext ctx)
    {
        if (ctx.Entities is null) return AreaRole.Unknown;
        foreach (var entity in ctx.Entities.Entries.Values)
        {
            if (entity.Name == "Map Device"
                || (!string.IsNullOrEmpty(entity.Path)
                    && entity.Path.Contains("MappingDevice", StringComparison.OrdinalIgnoreCase)))
                return AreaRole.SafeHub;
        }

        foreach (var entity in ctx.Entities.Entries.Values)
            if (entity.Kind is EntityListReader.EntityKind.Monster or EntityListReader.EntityKind.TownPortal)
                return AreaRole.Map;

        return AreaRole.Unknown;
    }
}
