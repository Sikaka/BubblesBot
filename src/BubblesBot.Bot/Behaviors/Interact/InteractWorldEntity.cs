using BubblesBot.Bot.Behaviors.Movement;
using BubblesBot.Bot.Input;
using BubblesBot.Bot.Settings;
using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;
using BubblesBot.Core.Pathfinding;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Behaviors.Interact;

/// <summary>
/// Generic walk-to-entity + click-its-label behavior. Used for shrines, ritual runes,
/// strongboxes, and any future "click this world object" interaction. Composes
/// <see cref="FollowPath"/> for navigation with <see cref="InteractSystem"/> for the
/// click + verify.
///
/// <para><b>Click target.</b> Resolves the entity's ground label rect (same channel that
/// loot uses). Falls back to the entity's projected world position when there's no label
/// — common for entities outside label rendering range.</para>
///
/// <para><b>Verification.</b> The supplied <paramref name="isActivated"/> predicate runs
/// after each click; once it returns true the behavior reports Success. Each entity-kind
/// supplies its own predicate (e.g. shrine: <c>IsOpened</c>; altar: state-machine flag).</para>
/// </summary>
public sealed class InteractWorldEntity : IBehavior
{
    private const int  ClickTimeoutMs      = 1500;
    private const int  ClickThrottleMs     = 600;
    private const int  MaxClickAttempts    = 4;

    private readonly InteractSystem _interact;
    private readonly MovementSystem _movement;
    private readonly FollowPath     _approach;
    private readonly Func<BehaviorContext, EntityCache.Entry?> _targetSelector;
    private readonly Func<BehaviorContext, EntityCache.Entry, bool> _isActivated;
    private readonly float? _interactionRangeGrid;
    private readonly bool _retryUntilActivated;

    private uint     _currentTargetId;
    private TimeSpan _lastClickAt = TimeSpan.MinValue;
    private TimeSpan _enteredRangeAt = TimeSpan.MinValue;
    private int      _attempts;
    private const int MovementSettleMs = 200;

    public string Name { get; }
    public BehaviorStatus LastStatus { get; private set; } = BehaviorStatus.Failure;
    public string LastDecision { get; private set; } = "init";

    public InteractWorldEntity(string name,
        InteractSystem interact, MovementSystem movement, SkillBook skills,
        Func<BehaviorContext, EntityCache.Entry?> targetSelector,
        Func<BehaviorContext, EntityCache.Entry, bool> isActivated,
        float? interactionRangeGrid = null,
        bool allowGapCrossing = true,
        bool retryUntilActivated = false)
    {
        Name = name;
        _interact = interact;
        _movement = movement;
        _targetSelector = targetSelector;
        _isActivated = isActivated;
        _interactionRangeGrid = interactionRangeGrid;
        _retryUntilActivated = retryUntilActivated;
        _approach = new FollowPath($"{name}/approach", movement,
            ctx => _targetSelector(ctx)?.GridPosition,
            skills, goalArrivalRadius: interactionRangeGrid,
            allowGapCrossing: allowGapCrossing);
    }

    public BehaviorStatus Tick(BehaviorContext ctx)
    {
        var target = _targetSelector(ctx);
        if (target is null) { LastDecision = "no target"; return LastStatus = BehaviorStatus.Failure; }

        // Reset attempt counter when target changes.
        if (target.Id != _currentTargetId)
        {
            BubblesBot.Bot.Diagnostics.EventLog.Log(Name, $"new target id={target.Id} path={target.Path} grid=({target.GridPosition.X},{target.GridPosition.Y})");
            _currentTargetId = target.Id;
            _attempts = 0;
            _enteredRangeAt = TimeSpan.MinValue;
        }

        // Already activated? Done.
        if (_isActivated(ctx, target))
        {
            BubblesBot.Bot.Diagnostics.EventLog.Log(Name, $"target activated id={target.Id}");
            // A persistent entity can be activated repeatedly (the Simulacrum monolith
            // starts every wave). Successful verification completes one interaction cycle,
            // so the next cycle needs a fresh retry budget.
            _attempts = 0;
            _lastClickAt = TimeSpan.MinValue;
            _enteredRangeAt = TimeSpan.MinValue;
            LastDecision = $"{target.Path} activated";
            return LastStatus = BehaviorStatus.Success;
        }

        var live = ctx.Live;
        if (live is null) { LastDecision = "no live player"; return LastStatus = BehaviorStatus.Failure; }
        var dist = Distance(live.Value.GridPosition, target.GridPosition);
        var interactionRange = _interactionRangeGrid ?? ctx.Settings.InteractionRangeGrid;
        var targeting = ctx.Snapshot.Nav.TargetingReader;
        var hasLineOfAccess = targeting is null
            || PathSmoother.HasLineOfSight(targeting,
                live.Value.GridPosition.X, live.Value.GridPosition.Y,
                target.GridPosition.X, target.GridPosition.Y,
                minValue: 1);
        var inRange = dist <= interactionRange && hasLineOfAccess;

        if (inRange)
        {
            // Halt movement BEFORE clicking. Without this the walk-skill key stays held
            // from the approach phase and the character runs in whatever direction the click
            // cursor lands — looks like wandering, often passes the entity entirely.
            // Release on every click-tick (cheap; no-op if already released).
            _movement.Release();

            if (_enteredRangeAt == TimeSpan.MinValue)
            {
                _enteredRangeAt = BotMonotonicClock.Now;
                LastDecision = $"in range; settling movement ({MovementSettleMs}ms)";
                return LastStatus = BehaviorStatus.Running;
            }
            if (BotMonotonicClock.ElapsedSince(_enteredRangeAt).TotalMilliseconds < MovementSettleMs)
            {
                LastDecision = $"settling movement before click ({MovementSettleMs}ms)";
                return LastStatus = BehaviorStatus.Running;
            }

            var clickPoint = ResolveClickPoint(ctx, target, live.Value);
            if (clickPoint is null)
            {
                BubblesBot.Bot.Diagnostics.EventLog.Log(Name, $"no click point: render={(target.RenderCompAddr != 0 ? "ok" : "missing")} cam={ctx.Snapshot.Camera.IsValid}");
                LastDecision = $"no clickable rect/projection for {target.Path}";
                return LastStatus = BehaviorStatus.Failure;
            }

            // Halt motion before clicking — drifting cursor while the click latch fires can
            // cause the click to land on the wrong screen pixel.
            if (BotMonotonicClock.ElapsedSince(_lastClickAt).TotalMilliseconds < ClickThrottleMs)
            {
                LastDecision = $"throttled ({_attempts}/{MaxClickAttempts})";
                return LastStatus = BehaviorStatus.Running;
            }
            if (_attempts >= MaxClickAttempts)
            {
                if (_retryUntilActivated)
                {
                    BubblesBot.Bot.Diagnostics.EventLog.Log(Name,
                        $"target id={target.Id} still available after {MaxClickAttempts} clicks; starting another verified retry cycle");
                    _attempts = 0;
                    _enteredRangeAt = BotMonotonicClock.Now;
                    LastDecision = $"retrying persistent target id={target.Id}";
                    return LastStatus = BehaviorStatus.Running;
                }
                LastDecision = $"max attempts on id={target.Id}";
                return LastStatus = BehaviorStatus.Failure;
            }

            var ticket = ctx.Input.Click(clickPoint.Value.X, clickPoint.Value.Y,
                ClickIntent.InteractWorld, $"{Name} {target.Path}",
                expectResolved: () => _isActivated(ctx, target), timeoutMs: ClickTimeoutMs);
            if (ticket.Accepted)
            {
                _lastClickAt = BotMonotonicClock.Now;
                _attempts++;
                BubblesBot.Bot.Diagnostics.EventLog.Log(Name, $"click sent abs=({clickPoint.Value.X},{clickPoint.Value.Y}) attempt {_attempts}/{MaxClickAttempts}");
                LastDecision = $"clicked {target.Path} (attempt {_attempts})";
            }
            else
            {
                BubblesBot.Bot.Diagnostics.EventLog.Log(Name, $"click suppressed by gate ({ctx.Input.GateState})");
                LastDecision = "click suppressed (gate)";
            }
            return LastStatus = BehaviorStatus.Running;
        }

        // Out of range → approach. FollowPath returns Success when at goal arrival; we map
        // that to Running so the parent Selector keeps picking us until we actually click.
        _enteredRangeAt = TimeSpan.MinValue;
        var status = _approach.Tick(ctx);
        if (_attempts == 0 && BotMonotonicClock.ElapsedSince(_lastApproachLogAt).TotalSeconds > 1.0)
        {
            BubblesBot.Bot.Diagnostics.EventLog.Log(Name, $"approaching id={target.Id} dist={dist:F1} (range={interactionRange})");
            _lastApproachLogAt = BotMonotonicClock.Now;
        }
        LastDecision = $"approaching {target.Path} dist={dist:F1} los={hasLineOfAccess}";
        return LastStatus = status == BehaviorStatus.Failure ? BehaviorStatus.Failure : BehaviorStatus.Running;
    }

    private TimeSpan _lastApproachLogAt = TimeSpan.MinValue;

    /// <summary>
    /// Resolve absolute screen coords to click. Preference order:
    /// <list type="number">
    ///   <item>The entity's ground label rect (waypoints, items, area transitions — anything
    ///         that surfaces in PoE's ground-label list).</item>
    ///   <item>Entity bounds projected via the camera matrix — for shrines, altars, ritual
    ///         runes, etc. We compute the entity's visual center as
    ///         <c>Render.Pos + Render.Bounds × 0.5</c>, then project + measure the on-screen
    ///         half-extents by projecting an X-offset and Y-offset world point. Click point
    ///         is randomized within this screen rect (humanlike + forgiving of overlap).</item>
    /// </list>
    /// Returns null when neither resolves.
    /// </summary>
    private (int X, int Y)? ResolveClickPoint(BehaviorContext ctx, EntityCache.Entry target, LivePlayer live)
    {
        var label = FindLabel(ctx, target.Id);
        if (label?.LabelRect is { } rect)
            return ctx.Snapshot.Window.ToScreen(rect.CenterX, rect.CenterY);

        // Click-target projection — two-stage approach:
        //   1. Standard `Camera.WorldToScreen(Pos + Bounds × 0.5)` matches ExileCore's
        //      `Render.InteractCenter` and is what AutoExile uses. Works for shrines,
        //      pumps, most actor-shaped entities — projects to where the cursor lands on
        //      the visual sprite.
        //   2. Fallback: project the entity's grid position at the PLAYER's world Z
        //      (same as Aim.AtGrid). Some entity types — notably blight chests — store
        //      `Render.Pos.Z` values that don't match the visible sprite (volume effects,
        //      animation pose data, etc.) and project way off-screen even when the player
        //      is right next to them. The grid projection at player Z always lands in
        //      sensible screen space and matches the actual click hitbox at ground level.
        var cam = ctx.Snapshot.Camera;
        if (!cam.IsValid) return null;

        var w_check = ctx.Snapshot.Window;
        bool OnScreen((float X, float Y)? p) => p is { } v
            && v.X >= 0 && v.Y >= 0 && v.X < w_check.Width && v.Y < w_check.Height;

        (float X, float Y)? center = null;

        if (target.RenderGeometryReadable)
        {
            var rPos = target.RenderPosition;
            var rBounds = target.RenderBounds;
            var centerWorld = new Vector3 { X = rPos.X + rBounds.X * 0.5f, Y = rPos.Y + rBounds.Y * 0.5f, Z = rPos.Z + rBounds.Z * 0.5f };
            center = cam.WorldToScreen(centerWorld);
        }

        if (!OnScreen(center))
        {
            var fallback = cam.GridToScreenAtPlayerZ(target.GridPosition, live.WorldPosition.Z);
            if (OnScreen(fallback))
            {
                BubblesBot.Bot.Diagnostics.EventLog.Log("ClickRect",
                    $"id={target.Id} bounds-center off-screen → grid-at-player-Z fallback ({fallback!.Value.X:F0},{fallback.Value.Y:F0})");
                center = fallback;
            }
            else
            {
                BubblesBot.Bot.Diagnostics.EventLog.Log("ClickRect",
                    $"id={target.Id} both projections off-screen — refusing click");
                return null;
            }
        }

        // Click strategy: first attempt clicks the EXACT projected center (the bot's best
        // guess at where the entity's interaction hitbox is). Each retry widens the
        // randomization radius — handles cases where another sprite (player, mob pack,
        // overlay) blocks the dead-center pixel. AutoExile pattern.
        var centerX = center!.Value.X;
        var centerY = center.Value.Y;
        if (_attempts == 0)
        {
            var w0 = ctx.Snapshot.Window;
            var cx = (int)Math.Clamp(centerX, 0, Math.Max(0, w0.Width  - 1));
            var cy = (int)Math.Clamp(centerY, 0, Math.Max(0, w0.Height - 1));

            if (BotMonotonicClock.ElapsedSince(_lastBoundsLogAt).TotalSeconds > 1.0)
            {
                BubblesBot.Bot.Diagnostics.EventLog.Log("ClickRect",
                    $"id={target.Id} center=({centerX:F0},{centerY:F0}) → click=center ({cx},{cy})");
                _lastBoundsLogAt = BotMonotonicClock.Now;
            }
            return w0.ToScreen(cx, cy);
        }

        // Retries: widen progressively. Half-extent scales with attempt number, capped at 30 px.
        var spread = MathF.Min(8f + _attempts * 8f, 30f);
        var rng = Random.Shared;
        var sx = (int)(centerX + (rng.NextSingle() * 2f - 1f) * spread);
        var sy = (int)(centerY + (rng.NextSingle() * 2f - 1f) * spread);
        var w = ctx.Snapshot.Window;
        sx = Math.Clamp(sx, 0, Math.Max(0, w.Width  - 1));
        sy = Math.Clamp(sy, 0, Math.Max(0, w.Height - 1));

        if (BotMonotonicClock.ElapsedSince(_lastBoundsLogAt).TotalSeconds > 1.0)
        {
            BubblesBot.Bot.Diagnostics.EventLog.Log("ClickRect",
                $"id={target.Id} attempt={_attempts} center=({centerX:F0},{centerY:F0}) spread={spread:F0} → click=({sx},{sy})");
            _lastBoundsLogAt = BotMonotonicClock.Now;
        }
        return w.ToScreen(sx, sy);
    }
    private static TimeSpan _lastBoundsLogAt = TimeSpan.MinValue;

    public void Reset()
    {
        _approach.Reset();
        _currentTargetId = 0;
        _attempts = 0;
        _lastClickAt = TimeSpan.MinValue;
        _enteredRangeAt = TimeSpan.MinValue;
        LastDecision = "reset";
        LastStatus = BehaviorStatus.Failure;
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static GroundLabelView? FindLabel(BehaviorContext ctx, uint entityId)
    {
        foreach (var l in ctx.Snapshot.GroundLabels)
            if (l.EntityId == entityId) return l;
        return null;
    }

    private static float Distance(Vector2i a, Vector2i b)
    {
        var dx = (float)(a.X - b.X);
        var dy = (float)(a.Y - b.Y);
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
