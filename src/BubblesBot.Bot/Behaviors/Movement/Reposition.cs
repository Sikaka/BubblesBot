using BubblesBot.Bot.Input;
using BubblesBot.Bot.Settings;
using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;

namespace BubblesBot.Bot.Behaviors.Movement;

/// <summary>
/// Fire a dash/blink toward a target cell as a one-shot reposition — the combat/dodge counterpart
/// to <see cref="FollowPath"/>'s internal gap-blink. Where FollowPath uses blinks to cross terrain
/// it can't walk, this exposes the dash as a deliberate mobility action: blink onto a fighting
/// spot, blink out of a telegraph, close a gap to a target. Same aim → settle → confirm shape,
/// with the minimum-cast-distance guard so short blinks don't silently fail.
///
/// <para>Dash selection: by default any ready <see cref="SkillRole.Dash"/> slot (not only
/// gap-crossers — a combat mobility dash need not be flagged <see cref="SkillSlot.CanCrossGaps"/>).
/// Returns Success once the character has moved <see cref="SuccessMove"/>+ cells, Failure when no
/// dash is ready / bound or after the aim window with the gate refusing, Running mid-dash.</para>
///
/// <para><b>Staged:</b> compiles and is self-contained, but not yet wired into a mode. Intended
/// consumers are the combat reposition step and the dodge lane, to be wired + live-validated.</para>
/// </summary>
public sealed class Reposition : IBehavior
{
    private const double AimMs = 120;
    private const double SettleMs = 380;
    private const float SuccessMove = 4f;
    private const float AimThrowGrid = 22f;

    private readonly MovementSystem _movement;
    private readonly SkillBook _skillBook;
    private readonly Func<BehaviorContext, Vector2i?> _targetSelector;
    private readonly bool _gapCrossersOnly;

    private enum Phase { Idle, Aim, Settle }
    private Phase _phase = Phase.Idle;
    private TimeSpan _phaseAt;
    private Vector2i _fromCell;

    public string Name { get; }
    public BehaviorStatus LastStatus { get; private set; } = BehaviorStatus.Failure;
    public string LastDecision { get; private set; } = "init";

    public Reposition(string name, MovementSystem movement, SkillBook skillBook,
        Func<BehaviorContext, Vector2i?> targetSelector, bool gapCrossersOnly = false)
    {
        Name = name;
        _movement = movement;
        _skillBook = skillBook;
        _targetSelector = targetSelector;
        _gapCrossersOnly = gapCrossersOnly;
    }

    private IEnumerable<SkillSlot> Dashes(BehaviorContext ctx)
        => _gapCrossersOnly ? ctx.Settings.Skills.GapCrossers : ctx.Settings.Skills.OfRole(SkillRole.Dash);

    public BehaviorStatus Tick(BehaviorContext ctx)
    {
        if (ctx.Live is not { } live) { LastDecision = "no live"; return LastStatus = BehaviorStatus.Failure; }
        var target = _targetSelector(ctx);
        if (target is null) { _phase = Phase.Idle; LastDecision = "no target"; return LastStatus = BehaviorStatus.Failure; }

        var player = live.GridPosition;
        var now = BotMonotonicClock.Now;

        if (_phase == Phase.Idle) { _phase = Phase.Aim; _phaseAt = now; _fromCell = player; }

        _movement.Release(this); // don't hold this behavior's walk during a dash

        // Throw the cursor across / toward the target so the dash fires at the right heading.
        var aim = ExtendAway(player, target.Value, AimThrowGrid);
        var scr = ctx.Snapshot.Camera.GridToScreenAtPlayerZ(aim, live.WorldPosition.Z);
        if (scr is { } s)
        {
            var (ax, ay) = ctx.Snapshot.Window.ToScreen(s.X, s.Y);
            ctx.Input.HoverAt(ax, ay, CursorPriority.BlinkAim);
        }

        if (_phase == Phase.Aim)
        {
            var dash = _skillBook.PickReady(Dashes(ctx));
            if (dash is null) { LastDecision = "waiting on dash charge"; return LastStatus = BehaviorStatus.Running; }
            if (dash.MinCastDistanceGrid > 0 && Distance(player, target.Value) < dash.MinCastDistanceGrid)
            { _phase = Phase.Idle; LastDecision = $"target too close ({Distance(player, target.Value):F0}<{dash.MinCastDistanceGrid})"; return LastStatus = BehaviorStatus.Failure; }
            if ((now - _phaseAt).TotalMilliseconds < AimMs) { LastDecision = "aiming"; return LastStatus = BehaviorStatus.Running; }

            var ticket = ctx.Input.TapKey(dash.Vk, ClickIntent.UseSkill, $"reposition {dash.Name}");
            if (!ticket.Accepted) { LastDecision = "gate refused"; return LastStatus = BehaviorStatus.Failure; }
            _skillBook.MarkCast(dash);
            _fromCell = player;
            _phase = Phase.Settle;
            _phaseAt = now;
            LastDecision = $"fired {dash.Name}";
            return LastStatus = BehaviorStatus.Running;
        }

        if ((now - _phaseAt).TotalMilliseconds < SettleMs) { LastDecision = "settling"; return LastStatus = BehaviorStatus.Running; }

        if (Distance(player, _fromCell) >= SuccessMove)
        {
            _phase = Phase.Idle;
            LastDecision = $"repositioned (moved {Distance(player, _fromCell):F0})";
            return LastStatus = BehaviorStatus.Success;
        }
        _phase = Phase.Idle;
        LastDecision = "no movement — failed";
        return LastStatus = BehaviorStatus.Failure;
    }

    public void Reset() { _phase = Phase.Idle; _movement.Release(this); LastStatus = BehaviorStatus.Failure; }

    private static Vector2i ExtendAway(Vector2i from, Vector2i toward, float dist)
    {
        var dx = (float)(toward.X - from.X);
        var dy = (float)(toward.Y - from.Y);
        var len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.5f) return toward;
        var k = dist / len;
        return new Vector2i { X = from.X + (int)MathF.Round(dx * k), Y = from.Y + (int)MathF.Round(dy * k) };
    }

    private static float Distance(Vector2i a, Vector2i b)
    {
        var dx = (float)(a.X - b.X);
        var dy = (float)(a.Y - b.Y);
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
