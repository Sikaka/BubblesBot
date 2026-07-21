using BubblesBot.Bot.Settings;
using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;

namespace BubblesBot.Bot.Behaviors.Movement;

/// <summary>
/// Channelled movement (archetype 2): travel by holding a movement-channel skill (Cyclone,
/// Whirling-Blades-style, RF-while-moving) aimed at the destination, so the character moves AND
/// deals damage at once — slower than a pure move skill but productive. Uses
/// <see cref="CombatSystem.HoldChannel"/> (hold key + per-tick cursor retarget) with an
/// <see cref="Aim.AtGrid"/> pointed at the goal cell.
///
/// <para><b>v1 is direct-aim</b> (channel toward the goal cell), not path-following: good for
/// short in-encounter repositioning where there's line of movement. Path-aware channelled travel
/// (feeding the channel successive A* nodes like <see cref="FollowPath"/> does for walking) is a
/// later refinement — the primitive is the same, only the aim source changes to the next node.</para>
///
/// <para><b>Cursor ownership:</b> when both this and a walk source could run, route both through
/// <see cref="CursorArbiter"/> (ChannelMove priority) so exactly one wins per tick. A mode that
/// uses MoveChannel as its sole movement source needs no arbiter.</para>
///
/// <para><b>Staged:</b> compiles; not wired into a mode. Requires a live pass to tune channel
/// speed vs. arrival and to validate the aim/hold interplay with real Cyclone.</para>
/// </summary>
public sealed class MoveChannel : IBehavior
{
    private readonly CombatSystem _combat;
    private readonly Func<BehaviorContext, SkillSlot?> _channelSelector;
    private readonly Func<BehaviorContext, Vector2i?> _goalSelector;
    private readonly float _arrivalRadius;
    private SkillSlot? _held;

    public string Name { get; }
    public BehaviorStatus LastStatus { get; private set; } = BehaviorStatus.Failure;
    public string LastDecision { get; private set; } = "init";

    /// <param name="channelSelector">Picks the movement-channel slot (default: profile's MovementChannel).</param>
    public MoveChannel(string name, CombatSystem combat,
        Func<BehaviorContext, Vector2i?> goalSelector,
        Func<BehaviorContext, SkillSlot?>? channelSelector = null,
        float arrivalRadius = 6f)
    {
        Name = name;
        _combat = combat;
        _goalSelector = goalSelector;
        _channelSelector = channelSelector ?? (ctx => ctx.Settings.Skills.MovementChannel);
        _arrivalRadius = arrivalRadius;
    }

    public BehaviorStatus Tick(BehaviorContext ctx)
    {
        if (ctx.Live is not { } live) { StopHeld(); LastDecision = "no live"; return LastStatus = BehaviorStatus.Failure; }

        var slot = _channelSelector(ctx);
        if (slot is null || slot.Vk == 0) { StopHeld(); LastDecision = "no channel slot"; return LastStatus = BehaviorStatus.Failure; }

        var goal = _goalSelector(ctx);
        if (goal is null) { StopHeld(); LastDecision = "no goal"; return LastStatus = BehaviorStatus.Failure; }

        var player = live.GridPosition;
        var dx = (float)(goal.Value.X - player.X);
        var dy = (float)(goal.Value.Y - player.Y);
        if (MathF.Sqrt(dx * dx + dy * dy) <= _arrivalRadius)
        {
            StopHeld();
            LastDecision = "arrived";
            return LastStatus = BehaviorStatus.Success;
        }

        _held = slot;
        var status = _combat.HoldChannel(slot, Aim.AtGrid(goal.Value), ctx);
        LastDecision = $"channel-move → ({goal.Value.X},{goal.Value.Y})";
        return LastStatus = status; // Running while channelling
    }

    private void StopHeld()
    {
        if (_held is not null) { _combat.StopChannel(_held); _held = null; }
    }

    public void Reset() { StopHeld(); LastStatus = BehaviorStatus.Failure; }
}
