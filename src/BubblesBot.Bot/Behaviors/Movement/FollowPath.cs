using BubblesBot.Bot.Input;
using BubblesBot.Bot.Settings;
using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;
using BubblesBot.Core.Pathfinding;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Behaviors.Movement;

/// <summary>
/// Walk along an A*-computed path to a goal. Recomputes when:
/// <list type="bullet">
///   <item>no current path,</item>
///   <item>the goal moved more than <see cref="GoalMovementThreshold"/> cells,</item>
///   <item>or the path is older than <see cref="MaxPathAge"/>.</item>
/// </list>
///
/// <para>Movement is delegated to <see cref="MovementSystem"/> — we hand it the next on-path
/// cell, it holds the move-skill key and aims the cursor. When the player gets within
/// <see cref="ArrivalRadius"/> of the current path index we advance to the next index. When
/// we're within <see cref="GoalArrivalRadius"/> of the final cell, return Success.</para>
///
/// <para>The smoothed path lets the cursor jump to long-distance LOS targets — the bot walks
/// in a straight line wherever it can see, then turns at corners. Without smoothing the
/// cursor would micro-jitter every few cells along the raw A* output.</para>
/// </summary>
public sealed class FollowPath : IBehavior
{
    /// <summary>Player must be within this many grid cells of the current path step to advance.</summary>
    private const float ArrivalRadius = 6f;

    /// <summary>If the goal moves by more than this many cells, recompute the path.</summary>
    private const float GoalMovementThreshold = 8f;

    /// <summary>Stale path → recompute even if everything else looks fine.</summary>
    private static readonly TimeSpan MaxPathAge = TimeSpan.FromSeconds(4);

    private readonly MovementSystem _movement;
    private readonly Func<BehaviorContext, Vector2i?> _goalSelector;
    private readonly SkillBook? _skillBook;
    private readonly bool _allowGapCrossing;
    /// <summary>
    /// How close to the final cell counts as "arrived." When null, falls back to the user's
    /// configured <see cref="BotSettings.InteractionRangeGrid"/> — appropriate for
    /// click-target goals (waypoints, NPCs, area transitions) so the bot stops walking the
    /// moment the click is viable. Pass an explicit value (e.g. 4) when the goal is a
    /// non-interactable waypoint and you want a tighter stop.
    /// </summary>
    private readonly float? _goalArrivalOverride;
    private readonly MovementProgressWatchdog _progress =
        new(2f, TimeSpan.FromMilliseconds(1200));

    // Blink execution state machine: aim the cursor well across the gap (clear angle) → hold the
    // aim briefly so the cursor actually moves → fire the dash → wait to confirm we crossed →
    // resume; retry up to MaxBlinkAttempts, then fall back to walking around the gap.
    private enum BlinkPhase { Idle, Aim, Settle }
    private BlinkPhase _blinkPhase = BlinkPhase.Idle;
    private TimeSpan _blinkPhaseAt;
    private Vector2i _blinkFromCell;
    private int _blinkAttempts;
    private TimeSpan _walkAroundUntil; // while in the future, recompute walk-only
    private const double BlinkAimMs = 140;       // hold the cursor across the gap before firing
    private const double BlinkSettleMs = 400;    // wait after firing to see if we actually crossed
    private const float  BlinkSuccessMove = 4f;  // grid cells moved that counts as a successful blink
    private const int    MaxBlinkAttempts = 3;   // failed blinks at one spot before walking around
    private const float  BlinkAimGrid = 22f;     // how far across the gap to throw the cursor

    private AStar? _astar;
    private int _astarW, _astarH;
    private IReadOnlyList<PathCell>? _path;
    private int _pathIndex;
    private Vector2i _pathGoal;
    private TimeSpan _pathBuiltAt;

    public string Name { get; }
    public BehaviorStatus LastStatus { get; private set; } = BehaviorStatus.Failure;
    public string LastDecision { get; private set; } = "init";
    public IReadOnlyList<PathCell>? CurrentPath => _path;
    public int CurrentPathIndex => _pathIndex;
    /// <summary>Current goal cell while a path is active — for test telemetry.</summary>
    public Vector2i? Goal => _path is not null ? _pathGoal : null;
    /// <summary>Cumulative count of accepted blink (gap-cross) taps — for test telemetry.</summary>
    public int BlinksFired { get; private set; }

    private readonly Func<BehaviorContext, float>? _goalArrivalProvider;

    public FollowPath(string name, MovementSystem movement, Func<BehaviorContext, Vector2i?> goalSelector,
        SkillBook? skillBook = null, float? goalArrivalRadius = null,
        Func<BehaviorContext, float>? goalArrivalRadiusProvider = null,
        bool allowGapCrossing = true)
    {
        Name = name;
        _movement = movement;
        _goalSelector = goalSelector;
        _skillBook = skillBook;
        _allowGapCrossing = allowGapCrossing;
        _goalArrivalOverride = goalArrivalRadius;
        _goalArrivalProvider = goalArrivalRadiusProvider;
    }

    public BehaviorStatus Tick(BehaviorContext ctx)
    {
        var live = ctx.Live;
        if (live is null) { LastDecision = "no live player"; return LastStatus = BehaviorStatus.Failure; }

        var goal = _goalSelector(ctx);
        if (goal is null) { _movement.Release(this); LastDecision = "no goal"; return LastStatus = BehaviorStatus.Failure; }

        var nav = ctx.Snapshot.Nav;
        if (!nav.IsAvailable || nav.PathReader is not { } pf)
        { _movement.Release(this); LastDecision = "no nav"; return LastStatus = BehaviorStatus.Failure; }

        var player = live.Value.GridPosition;

        var now = BotMonotonicClock.Now;
        var stuck = _progress.Observe(player, now);

        // A walk-only path cannot recover with a blink. Surface the watchdog failure to
        // the owning behavior so it can choose another goal instead of holding the move
        // key against the same blocked route forever (notably boss-arena staging).
        if (ShouldFailWalkOnlyPath(_allowGapCrossing, stuck))
        {
            _movement.Release(this);
            _path = null;
            LastDecision = "no movement on walk-only path";
            return LastStatus = BehaviorStatus.Failure;
        }

        // Goal-reached check. Distance alone is wrong (walls don't shrink Euclidean), and so
        // is targeting-LOS (gaps have tgt>0 but you can't walk through them). The right gate
        // is the *walkable* layer — every cell on the line must have pf>0. Anything else
        // forces A* to route around or use a Dash to blink across the gap.
        var goalArrival = _goalArrivalProvider?.Invoke(ctx)
                       ?? _goalArrivalOverride
                       ?? ctx.Settings.InteractionRangeGrid;
        if (Distance(player, goal.Value) <= goalArrival)
        {
            var hasLos = PathSmoother.HasLineOfSight(pf, player.X, player.Y, goal.Value.X, goal.Value.Y, minValue: 1);
            if (hasLos)
            {
                _movement.Halt(new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live), this);
                LastDecision = "arrived";
                return LastStatus = BehaviorStatus.Success;
            }
            // Close-but-blocked → keep walking. A* routes around or fires a blink.
        }

        // Decide whether the cached path is still good.
        var needRecompute = _path is null
            || _path.Count == 0
            || _pathIndex >= _path.Count
            || (now - _pathBuiltAt) > MaxPathAge
            || Distance(_pathGoal, goal.Value) > GoalMovementThreshold;

        if (needRecompute)
        {
            if (_astar is null || _astarW != nav.Width || _astarH != nav.Height)
            {
                _astar = new AStar(nav.Width, nav.Height);
                _astarW = nav.Width; _astarH = nav.Height;
            }
            // Build a gap plan from the user's settings + current dash readiness. No
            // gap-crossers configured / setting off → null plan, A* is in walk-only mode.
            // While walk-around is active (a gap blink just failed repeatedly), plan walk-only so
            // A* routes around instead of re-picking the same impossible blink.
            GapPlan? gap = null;
            if (_allowGapCrossing && ctx.Settings.AllowGapCrossing
                && _skillBook is not null && now >= _walkAroundUntil)
            {
                var crossers = ctx.Settings.Skills.GapCrossers;
                var totalReady = 0;
                var maxRange = 0;
                foreach (var c in crossers) { totalReady += _skillBook.ChargesReady(c); if (c.MaxRangeGrid > maxRange) maxRange = c.MaxRangeGrid; }
                if (maxRange > 0)
                {
                    // Penalty grows when no charges are ready — A* sees gap crossings as
                    // expensive and prefers walking around. With charges ready, blinks are
                    // cheap (only the gap-width term discourages overuse).
                    var penalty = totalReady > 0 ? 6f : 60f;
                    gap = new GapPlan { BlinkRange = maxRange, BlinkPenalty = penalty, LandingBuffer = 3, Enabled = true };
                }
            }

            var raw = _astar.FindPath(pf,
                new PathCell(player.X, player.Y),
                new PathCell(goal.Value.X, goal.Value.Y),
                maxNodes: ctx.Settings.PathfindingMaxNodes,
                gap: gap,
                targeting: ctx.Snapshot.Nav.TargetingReader);
            if (!raw.Found || raw.Cells.Count < 2)
            {
                _movement.Release(this);
                _path = null;
                LastDecision = "no path";
                return LastStatus = BehaviorStatus.Failure;
            }
            _path        = PathSmoother.Smooth(pf, raw.Cells);
            _pathIndex   = 1;        // skip cell 0 (current player position)
            _pathGoal    = goal.Value;
            _pathBuiltAt = now;
            _blinkPhase  = BlinkPhase.Idle; // abandon any in-flight blink against the old path
        }

        // Advance through reached OR walked-past path cells in one tick — handles LOS skips
        // where the player crossed several smoothed nodes between ticks, and corner-cutting
        // where the player passes a node outside ArrivalRadius. The walked-past rule (ported
        // from POE2Radar's RouteTracker) consumes a node once the player's offset from it has
        // a positive component toward the NEXT node; without it the cursor aims backward at a
        // missed node — including immediately after a recompute, since this loop also runs on
        // the fresh path in the same tick. Checking nodes sequentially (never global-nearest)
        // keeps switchbacks from collapsing the route through walls. Stop at Blink anchors so
        // the executor gets a chance to fire the dash on each one.
        while (_pathIndex < _path!.Count - 1
            && _path[_pathIndex].Action != StepAction.Blink
            && (Distance(player, _path[_pathIndex]) <= ArrivalRadius
                || WalkedPast(player, _path[_pathIndex], _path[_pathIndex + 1])))
        {
            _pathIndex++;
        }

        // Pre-advance into a Blink step: if the NEXT step is a blink, accept arrival on the
        // current step at a much larger radius. Otherwise the bot walks all the way up to a
        // wall (the typical pre-blink anchor sits right against it) and "humps" while the
        // arrival check waits for tight precision before firing the dash. We just want to be
        // close enough that the dash can land on the other side.
        while (_pathIndex < _path!.Count - 1
            && _path[_pathIndex].Action != StepAction.Blink
            && _path[_pathIndex + 1].Action == StepAction.Blink
            && Distance(player, _path[_pathIndex]) <= ArrivalRadius * 3f)
        {
            _pathIndex++;
        }

        var step = _path[_pathIndex];
        var stepGrid = new Vector2i { X = step.X, Y = step.Y };

        // Explicit A* blink step → run the blink state machine toward the landing cell.
        if (step.Action == StepAction.Blink && _skillBook is not null)
        {
            var outcome = RunBlink(ctx, player, stepGrid, $"step {_pathIndex}/{_path.Count - 1}");
            if (outcome == BlinkOutcome.Landed) _pathIndex++;          // crossed → advance past it
            else if (outcome == BlinkOutcome.GiveUp) BeginWalkAround(); // reroute on foot
            return LastStatus = BehaviorStatus.Running;
        }

        // Stuck on a WALK step (ledge/blocker the static nav layer can't see), or a recovery
        // blink already in progress → run the blink state machine to cross it.
        if (_allowGapCrossing && ctx.Settings.AllowGapCrossing
            && _skillBook is not null && (_blinkPhase != BlinkPhase.Idle || stuck))
        {
            var outcome = RunBlink(ctx, player, stepGrid, $"unstick {_pathIndex}/{_path.Count - 1}");
            if (outcome == BlinkOutcome.Landed) _progress.MarkProgress(player, now);
            else if (outcome == BlinkOutcome.GiveUp) { BeginWalkAround(); _progress.MarkProgress(player, now); }
            return LastStatus = BehaviorStatus.Running;
        }

        _movement.WalkToward(stepGrid, new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live), this);
        LastDecision = $"step {_pathIndex}/{_path.Count - 1} → ({step.X},{step.Y})";
        return LastStatus = BehaviorStatus.Running;
    }

    private enum BlinkOutcome { InProgress, Landed, GiveUp }

    /// <summary>
    /// Cross one gap as a sequence (state persists across ticks in the _blink* fields):
    /// release the walk key, throw the cursor well across the gap and hold it for
    /// <see cref="BlinkAimMs"/> so it actually moves and gives the dash a clear angle, fire the
    /// dash, wait <see cref="BlinkSettleMs"/>, then confirm the player actually moved
    /// (<see cref="BlinkSuccessMove"/>+ cells). Retries up to <see cref="MaxBlinkAttempts"/>,
    /// then returns <see cref="BlinkOutcome.GiveUp"/> so the caller can route around on foot.
    /// </summary>
    private BlinkOutcome RunBlink(BehaviorContext ctx, Vector2i player, Vector2i landing, string tag)
    {
        var now = BotMonotonicClock.Now;
        if (_blinkPhase == BlinkPhase.Idle)
        {
            _blinkPhase = BlinkPhase.Aim;
            _blinkPhaseAt = now;
            _blinkFromCell = player;
            _blinkAttempts = 0;
        }

        _movement.Release(this); // never hold this path's walk key during a blink

        // Keep the cursor thrown well across the gap so the dash fires at the right angle.
        var aim = ExtendAway(player, landing, BlinkAimGrid);
        var scr = ctx.Snapshot.Camera.GridToScreenAtPlayerZ(aim, ctx.Live!.Value.WorldPosition.Z);
        if (scr is { } s)
        {
            var (ax, ay) = ctx.Snapshot.Window.ToScreen(s.X, s.Y);
            ctx.Input.HoverAt(ax, ay, CursorPriority.BlinkAim);
        }

        if (_blinkPhase == BlinkPhase.Aim)
        {
            var dash = _skillBook!.PickReady(ctx.Settings.Skills.GapCrossers);
            if (dash is null) { LastDecision = $"{tag} BLINK waiting on charge"; return BlinkOutcome.InProgress; }

            // Minimum-cast-distance guard: PoE refuses / short-fires a blink below the skill's
            // floor (Frostblink), silently wasting the cast + a retry. If the landing is closer
            // than the dash's minimum, don't blink here — route on foot instead. Default
            // MinCastDistanceGrid == 0 disables the guard (unchanged behavior).
            if (dash.MinCastDistanceGrid > 0 && Distance(player, landing) < dash.MinCastDistanceGrid)
            {
                _blinkPhase = BlinkPhase.Idle;
                LastDecision = $"{tag} BLINK too short ({Distance(player, landing):F0}<{dash.MinCastDistanceGrid}) -> walk";
                return BlinkOutcome.GiveUp;
            }

            if ((now - _blinkPhaseAt).TotalMilliseconds < BlinkAimMs)
            { LastDecision = $"{tag} BLINK aiming"; return BlinkOutcome.InProgress; }

            var ticket = ctx.Input.TapKey(dash.Vk, ClickIntent.UseSkill, $"blink {dash.Name}");
            if (ticket.Accepted)
            {
                _skillBook.MarkCast(dash);
                BlinksFired++;
                _blinkFromCell = player;
                _blinkPhase = BlinkPhase.Settle;
                _blinkPhaseAt = now;
                LastDecision = $"{tag} BLINK fired ({dash.Name})";
            }
            else LastDecision = $"{tag} BLINK gate refused";
            return BlinkOutcome.InProgress;
        }

        // Settle: wait, then confirm we crossed.
        if ((now - _blinkPhaseAt).TotalMilliseconds < BlinkSettleMs)
        { LastDecision = $"{tag} BLINK settling"; return BlinkOutcome.InProgress; }

        if (Distance(player, _blinkFromCell) >= BlinkSuccessMove)
        {
            _blinkPhase = BlinkPhase.Idle;
            LastDecision = $"{tag} BLINK landed (moved {Distance(player, _blinkFromCell):F0})";
            return BlinkOutcome.Landed;
        }

        _blinkAttempts++;
        if (_blinkAttempts >= MaxBlinkAttempts)
        {
            _blinkPhase = BlinkPhase.Idle;
            LastDecision = $"{tag} BLINK failed x{_blinkAttempts} -> walk around";
            return BlinkOutcome.GiveUp;
        }
        _blinkPhase = BlinkPhase.Aim;
        _blinkPhaseAt = now;
        LastDecision = $"{tag} BLINK retry {_blinkAttempts}";
        return BlinkOutcome.InProgress;
    }

    /// <summary>A gap blink failed repeatedly — route on foot for a while before retrying gaps.</summary>
    private void BeginWalkAround()
    {
        _walkAroundUntil = BotMonotonicClock.Now.Add(TimeSpan.FromSeconds(8));
        _path = null; // force a walk-only recompute next tick
    }

    /// <summary>
    /// True when the player has crossed the perpendicular plane through <paramref name="node"/>
    /// facing <paramref name="next"/> — i.e. the node is behind them along the path direction,
    /// even if they cut the corner outside <see cref="ArrivalRadius"/>.
    /// </summary>
    private static bool WalkedPast(Vector2i player, PathCell node, PathCell next)
    {
        float abX = next.X - node.X, abY = next.Y - node.Y;
        if (abX * abX + abY * abY < 1e-3f) return true; // degenerate segment — consume it
        return (player.X - node.X) * abX + (player.Y - node.Y) * abY > 0f;
    }

    /// <summary>Point at least <paramref name="dist"/> cells from <paramref name="from"/> toward <paramref name="toward"/>.</summary>
    private static Vector2i ExtendAway(Vector2i from, Vector2i toward, float dist)
    {
        var dx = (float)(toward.X - from.X);
        var dy = (float)(toward.Y - from.Y);
        var len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.5f) return toward;
        var k = dist / len;
        return new Vector2i { X = from.X + (int)MathF.Round(dx * k), Y = from.Y + (int)MathF.Round(dy * k) };
    }

    public void Reset()
    {
        _movement.Release(this);
        _path = null;
        _pathIndex = 0;
        _progress.Reset();
        _blinkPhase = BlinkPhase.Idle;
        _walkAroundUntil = TimeSpan.Zero;
        LastStatus = BehaviorStatus.Failure;
    }

    private static float Distance(Vector2i a, Vector2i b)
    {
        var dx = (float)(a.X - b.X);
        var dy = (float)(a.Y - b.Y);
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static float Distance(Vector2i a, PathCell b)
        => Distance(a, new Vector2i { X = b.X, Y = b.Y });

    private static float Distance(PathCell a, Vector2i b)
        => Distance(new Vector2i { X = a.X, Y = a.Y }, b);

    internal static bool ShouldFailWalkOnlyPath(bool allowGapCrossing, bool stuck)
        => stuck && !allowGapCrossing;
}
