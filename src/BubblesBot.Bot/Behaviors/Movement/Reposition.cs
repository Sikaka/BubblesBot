using BubblesBot.Bot.Input;
using BubblesBot.Bot.Settings;
using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;

namespace BubblesBot.Bot.Behaviors.Movement;

/// <summary>
/// Fires a dash toward a selected combat position and confirms displacement before reporting
/// success. The configured cast time is a failure timeout, not an unconditional lockout: Blink
/// Arrow can visibly land much earlier than a conservative profile estimate, and combat must
/// resume on that observed landing edge.
/// </summary>
public sealed class Reposition : IBehavior
{
    private const double AimMs = 120;
    private const double SettleMs = 380;
    // Four-grid residual motion from the released walk key was observed before Blink Arrow
    // actually landed. Require a meaningful teleport-sized displacement before resuming Q.
    private const float SuccessMove = 20f;
    private const float FallbackAimGrid = 22f;

    private readonly MovementSystem _movement;
    private readonly SkillBook _skillBook;
    private readonly Func<BehaviorContext, Vector2i?> _targetSelector;
    private readonly bool _gapCrossersOnly;

    private enum Phase { Idle, Aim, Settle }
    private Phase _phase = Phase.Idle;
    private TimeSpan _phaseAt;
    private Vector2i _fromCell;
    private SkillSlot? _activeDash;

    public string Name { get; }
    public BehaviorStatus LastStatus { get; private set; } = BehaviorStatus.Failure;
    public string LastDecision { get; private set; } = "init";
    public bool IsActive => _phase != Phase.Idle;

    public Reposition(
        string name,
        MovementSystem movement,
        SkillBook skillBook,
        Func<BehaviorContext, Vector2i?> targetSelector,
        bool gapCrossersOnly = false)
    {
        Name = name;
        _movement = movement;
        _skillBook = skillBook;
        _targetSelector = targetSelector;
        _gapCrossersOnly = gapCrossersOnly;
    }

    private IEnumerable<SkillSlot> Dashes(BehaviorContext ctx)
        => _gapCrossersOnly
            ? ctx.Settings.Skills.GapCrossers
            : ctx.Settings.Skills.OfRole(SkillRole.Dash);

    public BehaviorStatus Tick(BehaviorContext ctx)
    {
        if (ctx.Live is not { } live)
        {
            Reset();
            LastDecision = "no live";
            return LastStatus = BehaviorStatus.Failure;
        }

        var player = live.GridPosition;
        var now = BotMonotonicClock.Now;

        // Blink Arrow lands after its attack animation. Do not abandon the reposition merely
        // because the player has not moved during the generic short dash settle window.
        if (_phase == Phase.Settle)
        {
            _movement.Release(this);
            var moved = Distance(player, _fromCell);
            if (moved >= SuccessMove)
            {
                var elapsedMs = (now - _phaseAt).TotalMilliseconds;
                var from = _fromCell;
                _phase = Phase.Idle;
                _activeDash = null;
                LastDecision = $"repositioned (moved {moved:F0})";
                Diagnostics.EventLog.Emit(
                    "combat", "combat.reposition-landed", Diagnostics.EventSeverity.Info,
                    $"reposition landed after {elapsedMs:F0}ms (moved {moved:F0})",
                    new Dictionary<string, object?>
                    {
                        ["fromX"] = from.X,
                        ["fromY"] = from.Y,
                        ["toX"] = player.X,
                        ["toY"] = player.Y,
                        ["movedGrid"] = moved,
                        ["elapsedMs"] = elapsedMs,
                    });
                return LastStatus = BehaviorStatus.Success;
            }

            var settleMs = Math.Max(SettleMs, (_activeDash?.CastTimeMs ?? 0) + 300);
            if ((now - _phaseAt).TotalMilliseconds < settleMs)
            {
                LastDecision = "settling";
                return LastStatus = BehaviorStatus.Running;
            }

            _phase = Phase.Idle;
            _activeDash = null;
            LastDecision = "no movement - failed";
            Diagnostics.EventLog.Emit(
                "combat", "combat.reposition-failed", Diagnostics.EventSeverity.Warning,
                $"reposition produced no displacement after {settleMs:F0}ms",
                new Dictionary<string, object?>
                {
                    ["fromX"] = _fromCell.X,
                    ["fromY"] = _fromCell.Y,
                    ["toX"] = player.X,
                    ["toY"] = player.Y,
                    ["movedGrid"] = moved,
                    ["timeoutMs"] = settleMs,
                });
            return LastStatus = BehaviorStatus.Failure;
        }

        var target = _targetSelector(ctx);
        if (target is null)
        {
            Reset();
            LastDecision = "no target";
            return LastStatus = BehaviorStatus.Failure;
        }

        if (_phase == Phase.Idle)
        {
            _activeDash = _skillBook.PickReady(Dashes(ctx));
            if (_activeDash is null)
            {
                LastDecision = "no ready dash";
                return LastStatus = BehaviorStatus.Failure;
            }
            _phase = Phase.Aim;
            _phaseAt = now;
            _fromCell = player;
        }

        _movement.Release(this);

        // Blink Arrow travels to its landing point. Use the configured range instead of the
        // old fixed 22-grid throw so an emergency reposition creates meaningful separation.
        var targetDistance = Distance(player, target.Value);
        var aimDistance = _activeDash!.MaxRangeGrid > 0
            ? Math.Min(targetDistance, _activeDash.MaxRangeGrid)
            : Math.Min(targetDistance, FallbackAimGrid);
        var aim = ExtendAway(player, target.Value, aimDistance);
        var screen = ctx.Snapshot.Camera.GridToScreenAtPlayerZ(aim, live.WorldPosition.Z);
        if (screen is { } s)
        {
            var (x, y) = ctx.Snapshot.Window.ToScreen(s.X, s.Y);
            ctx.Input.HoverAt(x, y, CursorPriority.BlinkAim);
        }

        var dash = _activeDash;
        if (dash.MinCastDistanceGrid > 0 && targetDistance < dash.MinCastDistanceGrid)
        {
            Reset();
            LastDecision = $"target too close ({targetDistance:F0}<{dash.MinCastDistanceGrid})";
            return LastStatus = BehaviorStatus.Failure;
        }
        if ((now - _phaseAt).TotalMilliseconds < AimMs)
        {
            LastDecision = "aiming";
            return LastStatus = BehaviorStatus.Running;
        }

        var ticket = ctx.Input.TapKey(dash.Vk, ClickIntent.UseSkill, $"reposition {dash.Name}");
        if (!ticket.Accepted)
        {
            Reset();
            LastDecision = "gate refused";
            return LastStatus = BehaviorStatus.Failure;
        }

        _skillBook.MarkCast(dash);
        _fromCell = player;
        _phase = Phase.Settle;
        _phaseAt = now;
        LastDecision = $"fired {dash.Name}";
        Diagnostics.EventLog.Emit(
            "combat", "combat.reposition-fired", Diagnostics.EventSeverity.Info,
            $"fired {dash.Name} toward ({aim.X},{aim.Y})",
            new Dictionary<string, object?>
            {
                ["skill"] = dash.Name,
                ["fromX"] = player.X,
                ["fromY"] = player.Y,
                ["aimX"] = aim.X,
                ["aimY"] = aim.Y,
                ["aimDistanceGrid"] = aimDistance,
                ["timeoutMs"] = Math.Max(SettleMs, dash.CastTimeMs + 300),
            });
        return LastStatus = BehaviorStatus.Running;
    }

    public void Reset()
    {
        _phase = Phase.Idle;
        _activeDash = null;
        _movement.Release(this);
        LastStatus = BehaviorStatus.Failure;
    }

    private static Vector2i ExtendAway(Vector2i from, Vector2i toward, float distance)
    {
        var dx = (float)(toward.X - from.X);
        var dy = (float)(toward.Y - from.Y);
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 0.5f) return toward;
        var scale = distance / length;
        return new Vector2i
        {
            X = from.X + (int)MathF.Round(dx * scale),
            Y = from.Y + (int)MathF.Round(dy * scale),
        };
    }

    private static float Distance(Vector2i a, Vector2i b)
    {
        var dx = (float)(a.X - b.X);
        var dy = (float)(a.Y - b.Y);
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
