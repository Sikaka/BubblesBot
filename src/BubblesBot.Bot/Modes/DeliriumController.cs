using BubblesBot.Bot.Behaviors;
using BubblesBot.Bot.Behaviors.Movement;
using BubblesBot.Bot.Input;
using BubblesBot.Bot.Strategies;
using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Modes;

/// <summary>
/// Per-map Delirium mirror lifecycle: route through the mirror, pace the fog front, manually end
/// after map objectives, and hold a delayed-reward barrier while the ordinary loot sweep drains
/// drops. Combat and incidental mechanics stay owned by <see cref="PushCombatMode"/>.
/// </summary>
public sealed class DeliriumController
{
    public enum Phase { Absent, NotPresent, Approach, Active, Throttling, Ending, RewardWait, Settled, Failed }

    private readonly MovementSystem _movement;
    private readonly FollowPath _approach;
    private readonly Func<GameSnapshot?> _getSnapshot;
    private Vector2i? _mirrorAnchor;
    private TimeSpan _activatedAt = TimeSpan.MinValue;
    private TimeSpan _endRequestedAt = TimeSpan.MinValue;
    private TimeSpan _buttonGoneAt = TimeSpan.MinValue;
    private TimeSpan _lastAcceptedLootAt = TimeSpan.MinValue;
    private int _lastAcceptedLootCount = -1;
    private bool _sawEndButton;
    private bool _endClickAccepted;
    private bool _recoveredCompletedMirror;
    private bool _completedOnFirstObservation;
    private TimeSpan _absenceObservedAt = TimeSpan.MinValue;

    public Phase CurrentPhase { get; private set; } = Phase.Absent;
    public string LastDecision { get; private set; } = "not observed";
    public bool IsEncounterActive => CurrentPhase is Phase.Active or Phase.Throttling;
    public bool SuppressBacktracking => CurrentPhase is Phase.Active or Phase.Throttling
        or Phase.Ending or Phase.RewardWait;
    public bool IsSettled => CurrentPhase is Phase.Absent or Phase.NotPresent or Phase.Settled or Phase.Failed;
    public Vector2i? MirrorAnchor => _mirrorAnchor;

    public DeliriumController(
        MovementSystem movement, SkillBook skills, Func<GameSnapshot?> getSnapshot)
    {
        _movement = movement;
        _getSnapshot = getSnapshot;
        _approach = new FollowPath(
            "cross Delirium mirror", movement, _ => _mirrorAnchor, skills,
            goalArrivalRadius: 1.5f);
    }

    private static DeliriumBlock? Config(BehaviorContext ctx)
    {
        var block = ctx.Strategy?.Block<DeliriumBlock>();
        return block is { Enabled: true } ? block : null;
    }

    public void Observe(BehaviorContext ctx)
    {
        if (Config(ctx) is null || ctx.Entities is null)
        {
            CurrentPhase = Phase.Absent;
            return;
        }

        var mirror = new MechanicsView(ctx.Entities).Entries
            .FirstOrDefault(entry => entry.Kind == MechanicKind.DeliriumMirror);
        if (mirror is null)
        {
            if (CurrentPhase == Phase.Absent && ctx.Entities.LastScanHealth.Healthy)
            {
                var now = BotMonotonicClock.Now;
                if (_absenceObservedAt == TimeSpan.MinValue) _absenceObservedAt = now;
                var elapsed = (now - _absenceObservedAt).TotalSeconds;
                if (DeliriumPresencePolicy.IsNotPresent(entityScanHealthy: true, elapsed))
                {
                    CurrentPhase = Phase.NotPresent;
                    LastDecision = $"no mirror present after {elapsed:F1}s";
                    Diagnostics.EventLog.Emit(
                        "delirium", "delirium.mirror-not-present", Diagnostics.EventSeverity.Info,
                        LastDecision);
                }
                else LastDecision = $"looking for mirror: {elapsed:F1}/{DeliriumPresencePolicy.MirrorDiscoverySeconds:F0}s";
            }
            return;
        }

        _absenceObservedAt = TimeSpan.MinValue;
        _mirrorAnchor ??= mirror.GridPosition;
        if (mirror.Status == MechanicStatus.Available && CurrentPhase is Phase.Absent or Phase.NotPresent)
        {
            CurrentPhase = Phase.Approach;
            LastDecision = $"mirror available at ({mirror.GridPosition.X},{mirror.GridPosition.Y})";
            return;
        }

        if (mirror.Status == MechanicStatus.Completed
            && CurrentPhase is Phase.Absent or Phase.NotPresent or Phase.Approach)
        {
            // NotPresent is also an unobserved-controller state: a cold attach can begin far
            // from the start, time out mirror discovery, then stream the completed mirror when
            // exploration loops back into its network bubble.
            var controllerWasAbsent = CurrentPhase is Phase.Absent or Phase.NotPresent;
            var distanceFromMirror = ctx.Live is { } live
                ? Distance(live.GridPosition, mirror.GridPosition)
                : 0;
            _completedOnFirstObservation = controllerWasAbsent;
            _recoveredCompletedMirror = ShouldTreatCompletedMirrorAsRestartRecovery(
                controllerWasAbsent, distanceFromMirror);
            _activatedAt = BotMonotonicClock.Now;
            CurrentPhase = Phase.Active;
            _approach.Reset();
            LastDecision = "mirror crossed; fog active";
            Diagnostics.EventLog.Emit(
                "delirium", "delirium.mirror-activated", Diagnostics.EventSeverity.Info,
                LastDecision,
                new Dictionary<string, object?>
                {
                    ["mirrorId"] = mirror.Id,
                    ["gridX"] = mirror.GridPosition.X,
                    ["gridY"] = mirror.GridPosition.Y,
                });
        }

        var endButton = ctx.Snapshot.DeliriumEndButton;
        if (endButton.IsVisible) _sawEndButton = true;
        if (CurrentPhase is Phase.Active or Phase.Throttling
            && _sawEndButton && !endButton.IsVisible)
        {
            // Natural fog expiry. Apply the same delayed-reward barrier as a manual end.
            _endRequestedAt = BotMonotonicClock.Now;
            _buttonGoneAt = _endRequestedAt;
            CurrentPhase = Phase.RewardWait;
            LastDecision = "fog ended naturally; waiting for rewards";
        }
        else if (ShouldSettleMissingEndButtonAfterColdAttach(
                     _recoveredCompletedMirror,
                     _completedOnFirstObservation,
                     BotMonotonicClock.ElapsedSince(_activatedAt).TotalSeconds)
                 && CurrentPhase is Phase.Active or Phase.Throttling
                 && !_sawEndButton
                 && !endButton.IsVisible)
        {
            // Process restart after an encounter already ended: the interacted mirror remains in
            // the area, but there is no end button. Visible rewards are still handled by the normal
            // loot branch, so do not impose a fresh 40-second barrier on an already-settled event.
            CurrentPhase = Phase.Settled;
            LastDecision = "recovered completed mirror with no active encounter";
        }
    }

    public bool WantsApproach(BehaviorContext ctx)
    {
        Observe(ctx);
        return CurrentPhase == Phase.Approach;
    }

    public BehaviorStatus ApproachTick(BehaviorContext ctx)
    {
        Observe(ctx);
        if (CurrentPhase != Phase.Approach || _mirrorAnchor is null)
            return BehaviorStatus.Failure;
        var status = _approach.Tick(ctx);
        LastDecision = $"approaching mirror: {_approach.LastDecision}";
        // Arriving near the centre can still need one more movement tick to cross the plane.
        if (status == BehaviorStatus.Success && ctx.Live is { } live)
        {
            var anchor = _mirrorAnchor.Value;
            var dx = anchor.X - live.GridPosition.X;
            var dy = anchor.Y - live.GridPosition.Y;
            if (dx * dx + dy * dy <= 9)
                _movement.WalkToward(
                    new Vector2i { X = anchor.X + 12 * Math.Sign(dx), Y = anchor.Y + 12 * Math.Sign(dy) },
                    new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live));
        }
        return BehaviorStatus.Running;
    }

    public bool ShouldThrottle(BehaviorContext ctx)
    {
        Observe(ctx);
        if (CurrentPhase is not (Phase.Active or Phase.Throttling)
            || Config(ctx) is not { } cfg
            || ctx.Live is not { } live
            || _mirrorAnchor is not { } anchor
            || _activatedAt == TimeSpan.MinValue)
            return false;
        var distance = Distance(live.GridPosition, anchor);
        var elapsed = (BotMonotonicClock.Now - _activatedAt).TotalSeconds;
        var throttle = DeliriumPacingPolicy.ShouldThrottle(
            distance, elapsed, cfg.InitialFogLeadGrid, cfg.MaxForwardGridPerSecond);
        CurrentPhase = throttle ? Phase.Throttling : Phase.Active;
        LastDecision = throttle
            ? $"holding fog front: {distance:F0}g at {elapsed:F1}s"
            : $"fog pace ok: {distance:F0}g at {elapsed:F1}s";
        return throttle;
    }

    public BehaviorStatus ThrottleTick(BehaviorContext ctx)
    {
        _movement.Halt(new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live));
        return BehaviorStatus.Running;
    }

    public bool ShouldFinalize(BehaviorContext ctx, bool objectivesComplete)
    {
        Observe(ctx);
        if (Config(ctx) is null) return false;
        if (CurrentPhase is Phase.Ending or Phase.RewardWait) return true;
        return objectivesComplete && CurrentPhase is Phase.Active or Phase.Throttling;
    }

    public BehaviorStatus FinalizeTick(BehaviorContext ctx)
    {
        Observe(ctx);
        var cfg = Config(ctx);
        if (cfg is null || CurrentPhase is Phase.Settled or Phase.Failed)
            return BehaviorStatus.Failure;

        _movement.Halt(new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live));
        var now = BotMonotonicClock.Now;
        if (CurrentPhase is Phase.Active or Phase.Throttling)
        {
            CurrentPhase = Phase.Ending;
            _endRequestedAt = now;
            LastDecision = "map objectives complete; requesting encounter end";
        }

        if (CurrentPhase == Phase.Ending)
        {
            var button = ctx.Snapshot.DeliriumEndButton;
            if (button.IsVisible && button.ClickRect is { } rect && !_endClickAccepted)
            {
                var (x, y) = ctx.Snapshot.Window.ToScreen((int)rect.CenterX, (int)rect.CenterY);
                var ticket = ctx.Input.Click(
                    x, y, ClickIntent.InteractUi, "end Delirium encounter",
                    expectResolved: () => !(_getSnapshot()?.DeliriumEndButton.IsVisible ?? true),
                    timeoutMs: 5000);
                if (ticket.Accepted)
                {
                    _endClickAccepted = true;
                    LastDecision = $"clicked end button child[{button.ChildIndex}]";
                    Diagnostics.EventLog.Emit(
                        "delirium", "delirium.end-requested", Diagnostics.EventSeverity.Info,
                        LastDecision);
                }
                return BehaviorStatus.Running;
            }

            if (!button.IsVisible && (_endClickAccepted || _sawEndButton))
            {
                _buttonGoneAt = now;
                CurrentPhase = Phase.RewardWait;
                LastDecision = "end accepted; delayed reward barrier active";
            }
            else if ((now - _endRequestedAt).TotalSeconds > cfg.EndButtonTimeoutSeconds)
            {
                CurrentPhase = Phase.Failed;
                LastDecision = $"end button unavailable/ambiguous ({button.VisibleLeagueButtons} visible)";
                Diagnostics.EventLog.Emit(
                    "delirium", "delirium.end-failed", Diagnostics.EventSeverity.Error,
                    LastDecision);
            }
            return BehaviorStatus.Running;
        }

        if (CurrentPhase == Phase.RewardWait)
        {
            var accepted = AcceptedLootCount(ctx);
            if (accepted != _lastAcceptedLootCount)
            {
                _lastAcceptedLootCount = accepted;
                _lastAcceptedLootAt = now;
            }
            var waited = (now - _buttonGoneAt).TotalSeconds;
            var quiet = _lastAcceptedLootAt == TimeSpan.MinValue
                ? waited
                : (now - _lastAcceptedLootAt).TotalSeconds;
            LastDecision = $"reward wait {waited:F1}/{cfg.MinimumRewardWaitSeconds}s; loot={accepted} quiet={quiet:F1}s";
            if (waited >= cfg.MinimumRewardWaitSeconds
                && accepted == 0
                && quiet >= cfg.RewardQuietSeconds)
            {
                CurrentPhase = Phase.Settled;
                LastDecision = "Delirium rewards drained";
                Diagnostics.EventLog.Emit(
                    "delirium", "delirium.rewards-settled", Diagnostics.EventSeverity.Info,
                    LastDecision,
                    new Dictionary<string, object?> { ["waitSeconds"] = waited });
            }
            else if (waited >= cfg.MaximumRewardWaitSeconds)
            {
                CurrentPhase = Phase.Failed;
                LastDecision = "Delirium reward wait timed out";
                Diagnostics.EventLog.Emit(
                    "delirium", "delirium.reward-timeout", Diagnostics.EventSeverity.Error,
                    LastDecision,
                    new Dictionary<string, object?> { ["waitSeconds"] = waited, ["acceptedLoot"] = accepted });
            }
            return BehaviorStatus.Running;
        }

        return BehaviorStatus.Running;
    }

    private static int AcceptedLootCount(BehaviorContext ctx)
    {
        var filter = Behaviors.Loot.LootClosestVisible.SharedValueFilter;
        return ctx.Snapshot.GroundLabels.Count(label =>
            label.IsItem
            && label.IsLabelVisible
            && label.DistanceToPlayer <= ctx.Settings.LootRangeGrid
            && (filter is null || filter.Evaluate(label, ctx.Settings.Loot).ShouldTake));
    }

    public object Telemetry(BehaviorContext ctx) => new
    {
        phase = CurrentPhase.ToString(),
        decision = LastDecision,
        active = IsEncounterActive,
        settled = IsSettled,
        endButtonVisible = ctx.Snapshot.DeliriumEndButton.IsVisible,
        endButtonIndex = ctx.Snapshot.DeliriumEndButton.ChildIndex,
        mirror = _mirrorAnchor is { } p ? new { x = p.X, y = p.Y } : null,
    };

    public void Reset()
    {
        _approach.Reset();
        _mirrorAnchor = null;
        _activatedAt = TimeSpan.MinValue;
        _endRequestedAt = TimeSpan.MinValue;
        _buttonGoneAt = TimeSpan.MinValue;
        _lastAcceptedLootAt = TimeSpan.MinValue;
        _lastAcceptedLootCount = -1;
        _sawEndButton = false;
        _endClickAccepted = false;
        _recoveredCompletedMirror = false;
        _completedOnFirstObservation = false;
        _absenceObservedAt = TimeSpan.MinValue;
        CurrentPhase = Phase.Absent;
        LastDecision = "reset";
    }

    internal static bool ShouldTreatCompletedMirrorAsRestartRecovery(
        bool controllerWasAbsent, float distanceFromMirror)
        => controllerWasAbsent && distanceFromMirror >= 300f;

    internal static bool ShouldSettleMissingEndButtonAfterColdAttach(
        bool farRestartRecovery, bool completedOnFirstObservation, double elapsedSeconds)
        => farRestartRecovery
            ? elapsedSeconds >= 3
            : completedOnFirstObservation && elapsedSeconds >= 10;

    private static float Distance(Vector2i a, Vector2i b)
    {
        var dx = (float)(a.X - b.X);
        var dy = (float)(a.Y - b.Y);
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
