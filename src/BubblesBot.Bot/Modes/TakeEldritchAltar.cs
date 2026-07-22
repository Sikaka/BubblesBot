using BubblesBot.Bot.Behaviors;
using BubblesBot.Bot.Behaviors.Movement;
using BubblesBot.Bot.Input;
using BubblesBot.Bot.Settings;
using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;
using BubblesBot.Core.Pathfinding;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Modes;

/// <summary>
/// Walk to an unconsumed Eldritch altar, read its two-choice tree off the ground label,
/// pick a side per <c>Settings.AltarPolicy</c> (see <see cref="EldritchAltarScoring.Decide"/>),
/// click the chosen button rect, and confirm consumption by the choice tree disappearing.
///
/// <para>Differs from <see cref="Behaviors.Interact.InteractWorldEntity"/> in two ways that
/// justify a dedicated controller: the click target is one of two label-child rects (not the
/// label center / entity projection), and a decision step sits between arrival and click —
/// including a Skip outcome that must permanently blacklist the altar for this map.</para>
///
/// <para>Every altar is recorded in <see cref="EldritchAltarLedger"/> exactly once — taken,
/// skipped by policy, unreadable (fail-closed), or given up after max clicks — so the
/// behavior never loops on one altar and <see cref="HasCandidate"/> eventually goes false.
/// The ledger is external and area-keyed because composers Reset() this node on every
/// interruption; any memory held here would be wiped tick-by-tick.</para>
/// </summary>
public sealed class TakeEldritchAltar : IBehavior
{
    private const int ClickTimeoutMs = 1500;
    private const int ClickThrottleMs = 600;
    private const int MaxClickAttempts = 4;
    private const int MovementSettleMs = 200;
    private const int UnreadableGiveUpMs = 4000;

    private readonly MovementSystem _movement;
    private readonly FollowPath _approach;
    private readonly Func<GameSnapshot?> _getSnapshot;
    private readonly Func<BehaviorContext, IEnumerable<MechanicEntry>> _candidates;

    private uint _currentTargetId;
    private int _attempts;
    private EldritchAltarScoring.AltarDecision _chosenSide;
    private TimeSpan _lastClickAt = TimeSpan.MinValue;
    private TimeSpan _enteredRangeAt = TimeSpan.MinValue;
    private TimeSpan _unreadableSince = TimeSpan.MinValue;

    public string Name { get; }
    public BehaviorStatus LastStatus { get; private set; } = BehaviorStatus.Failure;
    public string LastDecision { get; private set; } = "init";

    public TakeEldritchAltar(string name, MovementSystem movement, SkillBook skills,
        Func<GameSnapshot?> getSnapshot,
        Func<BehaviorContext, IEnumerable<MechanicEntry>> candidates)
    {
        Name = name;
        _movement = movement;
        _getSnapshot = getSnapshot;
        _candidates = candidates;
        _approach = new FollowPath($"{name}/approach", movement,
            ctx => Next(ctx)?.GridPosition, skills,
            goalArrivalRadiusProvider: ctx => ctx.Settings.InteractionRangeGrid);
    }

    /// <summary>Gate condition for the mode's behavior tree.</summary>
    public bool HasCandidate(BehaviorContext ctx) => Next(ctx) is not null;

    /// <summary>Nearest unresolved altar — exposed so the mode's interaction sweep can
    /// compare its distance against loot and shrines.</summary>
    public MechanicEntry? NextCandidate(BehaviorContext ctx) => Next(ctx);

    private MechanicEntry? Next(BehaviorContext ctx)
    {
        if (ctx.Live is not { } live) return null;
        MechanicEntry? best = null;
        long bestD2 = long.MaxValue;
        foreach (var altar in _candidates(ctx))
        {
            if (EldritchAltarLedger.IsResolved(ctx.Snapshot.AreaHash, altar.Id)) continue;
            long dx = altar.GridPosition.X - live.GridPosition.X;
            long dy = altar.GridPosition.Y - live.GridPosition.Y;
            var d2 = dx * dx + dy * dy;
            if (d2 < bestD2) { bestD2 = d2; best = altar; }
        }
        return best;
    }

    public BehaviorStatus Tick(BehaviorContext ctx)
    {
        var maxClickAttempts = LatencyPolicy.RetryLimit(MaxClickAttempts, ctx.Settings);
        var target = Next(ctx);
        if (target is null) { LastDecision = "no target"; return LastStatus = BehaviorStatus.Failure; }
        if (ctx.Live is not { } live) { LastDecision = "no live player"; return LastStatus = BehaviorStatus.Failure; }

        if (target.Id != _currentTargetId)
        {
            Diagnostics.EventLog.Log(Name,
                $"new target id={target.Id} path={target.Path} grid=({target.GridPosition.X},{target.GridPosition.Y})");
            _currentTargetId = target.Id;
            _attempts = 0;
            _chosenSide = EldritchAltarScoring.AltarDecision.Skip;
            _enteredRangeAt = TimeSpan.MinValue;
            _unreadableSince = TimeSpan.MinValue;
        }

        var dist = Distance(live.GridPosition, target.GridPosition);
        var targeting = ctx.Snapshot.Nav.TargetingReader;
        var hasLineOfAccess = targeting is null
            || PathSmoother.HasLineOfSight(targeting,
                live.GridPosition.X, live.GridPosition.Y,
                target.GridPosition.X, target.GridPosition.Y,
                minValue: 1);
        var inRange = dist <= ctx.Settings.InteractionRangeGrid && hasLineOfAccess;

        if (!inRange)
        {
            _enteredRangeAt = TimeSpan.MinValue;
            _unreadableSince = TimeSpan.MinValue;
            var status = _approach.Tick(ctx);
            LastDecision = $"approaching altar {target.Id} dist={dist:F1} los={hasLineOfAccess}";
            return LastStatus = status == BehaviorStatus.Failure ? BehaviorStatus.Failure : BehaviorStatus.Running;
        }

        // Same settle pattern as InteractWorldEntity: release the walk key and let the
        // character stop before reading rects / clicking, or the click lands mid-stride.
        _movement.Release();
        var now = BotMonotonicClock.Now;
        if (_enteredRangeAt == TimeSpan.MinValue)
        {
            _enteredRangeAt = now;
            LastDecision = "in range; settling";
            return LastStatus = BehaviorStatus.Running;
        }
        if (BotMonotonicClock.ElapsedSince(_enteredRangeAt).TotalMilliseconds < MovementSettleMs)
        {
            LastDecision = "settling before read";
            return LastStatus = BehaviorStatus.Running;
        }

        var choices = FindChoices(ctx.Snapshot, target.Id);

        // Post-click confirmation: we clicked and the choice tree is gone → consumed.
        if (_attempts > 0 && choices is null)
        {
            Resolve(ctx, target, taken: true, $"altar {target.Id} consumed (choice {_chosenSide})");
            return LastStatus = BehaviorStatus.Success;
        }

        if (choices is null)
        {
            // In range but no readable choice tree. Give the label a moment to render,
            // then fail closed — never click what we can't read.
            if (_unreadableSince == TimeSpan.MinValue) _unreadableSince = now;
            if (BotMonotonicClock.ElapsedSince(_unreadableSince).TotalMilliseconds < UnreadableGiveUpMs)
            {
                LastDecision = $"altar {target.Id} choices not readable yet";
                return LastStatus = BehaviorStatus.Running;
            }
            Resolve(ctx, target, taken: false, $"altar {target.Id} choices unreadable in range; skipping (fail closed)");
            return LastStatus = BehaviorStatus.Failure;
        }
        _unreadableSince = TimeSpan.MinValue;

        // Decide once per altar; rects are re-read every click because they track the camera.
        if (_chosenSide == EldritchAltarScoring.AltarDecision.Skip && _attempts == 0)
        {
            // Policy + per-strategy weight overrides come from the active strategy's altar block.
            var altarCfg = ctx.Strategy?.Block<Strategies.EldritchAltarsBlock>();
            var policy = altarCfg is { Enabled: true } ? (int)altarCfg.Policy : 0;
            var overrides = altarCfg is { WeightOverrides.Count: > 0 } ? altarCfg.WeightOverrides : null;
            var verdict = EldritchAltarScoring.Decide(
                policy, choices.Top.Text, choices.Bottom.Text, overrides);
            LogVerdict(target, verdict);
            if (verdict.Decision == EldritchAltarScoring.AltarDecision.Skip)
            {
                Resolve(ctx, target, taken: false, $"altar {target.Id} skipped: {verdict.Reason}");
                return LastStatus = BehaviorStatus.Failure;
            }
            _chosenSide = verdict.Decision;
        }

        // ElapsedSince, not raw subtraction: _lastClickAt starts at the MinValue sentinel,
        // and (Now - TimeSpan.MinValue) throws OverflowException (live incident 2026-07-15).
        if (BotMonotonicClock.ElapsedSince(_lastClickAt).TotalMilliseconds < ClickThrottleMs)
        {
            LastDecision = $"throttled ({_attempts}/{maxClickAttempts})";
            return LastStatus = BehaviorStatus.Running;
        }
        if (_attempts >= maxClickAttempts)
        {
            Resolve(ctx, target, taken: false, $"altar {target.Id} still readable after {maxClickAttempts} clicks; giving up");
            return LastStatus = BehaviorStatus.Failure;
        }

        var choice = _chosenSide == EldritchAltarScoring.AltarDecision.Top ? choices.Top : choices.Bottom;
        var (sx, sy) = ctx.Snapshot.Window.ToScreen(choice.ClickRect.CenterX, choice.ClickRect.CenterY);
        var targetId = target.Id;
        var ticket = ctx.Input.Click(sx, sy, ClickIntent.InteractWorld,
            $"{Name} {_chosenSide} of altar {targetId}",
            expectResolved: () => ChoicesGone(targetId), timeoutMs: ClickTimeoutMs);
        if (ticket.Accepted)
        {
            _lastClickAt = now;
            _attempts++;
            Diagnostics.EventLog.Log(Name,
                $"clicked {_chosenSide} of altar {targetId} abs=({sx},{sy}) attempt {_attempts}/{maxClickAttempts}");
            LastDecision = $"clicked {_chosenSide} (attempt {_attempts})";
        }
        else
        {
            LastDecision = "click suppressed (gate)";
        }
        return LastStatus = BehaviorStatus.Running;
    }

    private void Resolve(BehaviorContext ctx, MechanicEntry target, bool taken, string message)
    {
        EldritchAltarLedger.Mark(ctx.Snapshot.AreaHash, target.Id, taken);
        Diagnostics.EventLog.Emit("mechanic", taken ? "mechanic.altar-taken" : "mechanic.altar-skipped",
            Diagnostics.EventSeverity.Info, message,
            new Dictionary<string, object?>
            {
                ["altarId"] = target.Id,
                ["path"] = target.Path,
                ["choice"] = _chosenSide.ToString(),
                ["attempts"] = _attempts,
            });
        LastDecision = message;
    }

    private void LogVerdict(MechanicEntry target, EldritchAltarScoring.AltarVerdict verdict)
    {
        Diagnostics.EventLog.Emit("mechanic", "mechanic.altar-decision", Diagnostics.EventSeverity.Info,
            $"altar {target.Id}: {verdict.Decision} ({verdict.Reason}) " +
            $"top(reward={verdict.Top.Reward} total={verdict.Top.Total} veto={verdict.Top.Vetoed}) " +
            $"bottom(reward={verdict.Bottom.Reward} total={verdict.Bottom.Total} veto={verdict.Bottom.Vetoed})",
            new Dictionary<string, object?>
            {
                ["altarId"] = target.Id,
                ["decision"] = verdict.Decision.ToString(),
                ["reason"] = verdict.Reason,
                ["topUnknown"] = verdict.Top.UnknownLines,
                ["bottomUnknown"] = verdict.Bottom.UnknownLines,
            });
        // Unrecognized mod lines are the weight table's growth loop — keep them loud.
        foreach (var line in verdict.Top.UnknownLines.Concat(verdict.Bottom.UnknownLines))
            Diagnostics.EventLog.Log(Name, $"unweighted altar mod: '{line}'");
    }

    private static EldritchAltarChoiceSet? FindChoices(GameSnapshot snapshot, uint entityId)
    {
        foreach (var label in snapshot.GroundLabels)
            if (label.EntityId == entityId)
                return label.EldritchAltarChoices;
        return null;
    }

    private bool ChoicesGone(uint entityId)
    {
        var snapshot = _getSnapshot();
        if (snapshot is null) return false;
        return FindChoices(snapshot, entityId) is null;
    }

    /// <summary>
    /// Interruption cleanup ONLY — composers call this every tick the node is deselected
    /// (If-gate false, higher Selector branch running). Which altars are already handled
    /// lives in <see cref="EldritchAltarLedger"/> precisely so it survives this.
    /// </summary>
    public void Reset()
    {
        _approach.Reset();
        _currentTargetId = 0;
        _attempts = 0;
        _chosenSide = EldritchAltarScoring.AltarDecision.Skip;
        _lastClickAt = TimeSpan.MinValue;
        _enteredRangeAt = TimeSpan.MinValue;
        _unreadableSince = TimeSpan.MinValue;
        LastDecision = "reset";
        LastStatus = BehaviorStatus.Failure;
    }

    private static float Distance(Vector2i a, Vector2i b)
    {
        var dx = (float)(a.X - b.X);
        var dy = (float)(a.Y - b.Y);
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
