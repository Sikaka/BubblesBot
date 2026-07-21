using BubblesBot.Bot.Behaviors;
using BubblesBot.Bot.Input;
using BubblesBot.Bot.Settings;

namespace BubblesBot.Bot.Systems;

/// <summary>
/// Cross-cutting facade for skill execution. Takes <see cref="SkillSlot"/> references
/// directly — slots are not addressed by integer index since the profile is variable-length.
/// Resolves the slot's VK + the supplied <see cref="Aim"/>, parks the cursor, and either taps
/// the key (Cast) or holds it for as long as the predicate is true (Channel).
///
/// <para>Cooldown / charge accounting lives in <see cref="SkillBook"/>; this facade just
/// emits input and reports whether the gate accepted it. The behavior tree wraps Cast in
/// <see cref="Cooldown"/> or <see cref="If"/> on a SkillBook readiness check before deciding
/// to fire.</para>
/// </summary>
public sealed class CombatSystem
{
    private readonly Dictionary<SkillSlot, IInputHandle> _channels = new();

    /// <summary>
    /// Optional action-lock tracker. When set, a successful <see cref="Cast"/> or
    /// <see cref="HoldChannel"/> of a slot with <see cref="SkillSlot.LockMs"/> &gt; 0 records the
    /// commitment so movement/dodge logic can see it. Null (the default) records nothing —
    /// existing behavior is unchanged.
    /// </summary>
    public ActionState? ActionState { get; set; }

    /// <summary>
    /// Aim + tap the slot's hotkey. Returns Failure when the slot is unbound, the aim
    /// resolved to nothing, or the input gate refused the tap.
    /// </summary>
    public BehaviorStatus Cast(SkillSlot slot, Aim aim, BehaviorContext ctx, string description)
    {
        if (slot.Vk == 0) return BehaviorStatus.Failure;
        var aimPt = aim.Resolve(ctx);
        if (aimPt is null) return BehaviorStatus.Failure;

        ctx.Input.HoverAt(aimPt.Value.X, aimPt.Value.Y, CursorPriority.CombatAim);
        var ticket = ctx.Input.TapKey(slot.Vk, ClickIntent.UseSkill, description);
        if (!ticket.Accepted) return BehaviorStatus.Failure;
        ActionState?.OnFired(slot);
        return BehaviorStatus.Success;
    }

    /// <summary>
    /// Begin (or refresh) holding the slot's hotkey. Cursor re-points at the aim each tick
    /// so a held channelled skill tracks a moving target. Caller is responsible for releasing
    /// — by stopping refresh (TTL fires) or by calling <see cref="StopChannel"/>.
    /// </summary>
    public BehaviorStatus HoldChannel(SkillSlot slot, Aim aim, BehaviorContext ctx)
    {
        if (slot.Vk == 0) return BehaviorStatus.Failure;
        var aimPt = aim.Resolve(ctx);
        if (aimPt is null) { StopChannel(slot); return BehaviorStatus.Failure; }

        ctx.Input.HoverAt(aimPt.Value.X, aimPt.Value.Y, CursorPriority.CombatAim);
        if (_channels.TryGetValue(slot, out var existing) && existing.IsActive)
            existing.Refresh();
        else
            _channels[slot] = ctx.Input.BeginHoldKey(slot.Vk, HoldBudget.Default);

        return BehaviorStatus.Running;
    }

    public void StopChannel(SkillSlot slot)
    {
        if (!_channels.TryGetValue(slot, out var h)) return;
        h.Release();
        _channels.Remove(slot);
    }

    public void StopAllChannels()
    {
        foreach (var h in _channels.Values) h.Release();
        _channels.Clear();
    }
}
