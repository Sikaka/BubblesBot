using BubblesBot.Bot.Settings;
using BubblesBot.Bot.Systems;

namespace BubblesBot.Bot.Behaviors.Combat;

/// <summary>
/// Tap a skill once. Returns Success on a successful tap, Failure when the gate refused or
/// no aim resolved. The slot reference comes from the user's <see cref="SkillProfile"/> —
/// behaviors look it up by role+name (or pin a specific instance) and pass it in.
/// </summary>
public sealed class Cast : IBehavior
{
    private readonly CombatSystem _combat;
    private readonly Func<BehaviorContext, SkillSlot?> _slotPicker;
    private readonly Aim _aim;
    private readonly SkillBook? _book;
    public string Name { get; }
    public BehaviorStatus LastStatus { get; private set; } = BehaviorStatus.Failure;

    public Cast(string name, CombatSystem combat, Func<BehaviorContext, SkillSlot?> slotPicker, Aim aim, SkillBook? book = null)
    {
        Name = name; _combat = combat; _slotPicker = slotPicker; _aim = aim; _book = book;
    }

    public BehaviorStatus Tick(BehaviorContext ctx)
    {
        var slot = _slotPicker(ctx);
        if (slot is null) return LastStatus = BehaviorStatus.Failure;

        // Real-cooldown / charge / min-interval gate. Without this Cast fires blindly and
        // queues key taps that PoE silently ignores — which doesn't break anything but hides
        // when a skill is genuinely on cooldown vs. when something else is wrong.
        if (_book is not null && !_book.IsReady(slot)) return LastStatus = BehaviorStatus.Failure;

        var result = _combat.Cast(slot, _aim, ctx, Name);
        if (result == BehaviorStatus.Success) _book?.MarkCast(slot);
        return LastStatus = result;
    }
}

/// <summary>
/// Hold a skill's key while <paramref name="continueWhile"/> is true. Cursor re-aims each
/// tick. Releases on Reset.
/// </summary>
public sealed class Channel : IBehavior
{
    private readonly CombatSystem _combat;
    private readonly Func<BehaviorContext, SkillSlot?> _slotPicker;
    private readonly Aim _aim;
    private readonly Func<BehaviorContext, bool> _continueWhile;
    private SkillSlot? _heldSlot;
    public string Name { get; }
    public BehaviorStatus LastStatus { get; private set; } = BehaviorStatus.Failure;

    public Channel(string name, CombatSystem combat, Func<BehaviorContext, SkillSlot?> slotPicker, Aim aim, Func<BehaviorContext, bool> continueWhile)
    {
        Name = name; _combat = combat; _slotPicker = slotPicker; _aim = aim; _continueWhile = continueWhile;
    }

    public BehaviorStatus Tick(BehaviorContext ctx)
    {
        if (!_continueWhile(ctx))
        {
            if (_heldSlot is not null) { _combat.StopChannel(_heldSlot); _heldSlot = null; }
            return LastStatus = BehaviorStatus.Success;
        }

        var slot = _slotPicker(ctx);
        if (slot is null) return LastStatus = BehaviorStatus.Failure;
        _heldSlot = slot;
        return LastStatus = _combat.HoldChannel(slot, _aim, ctx);
    }

    public void Reset()
    {
        if (_heldSlot is not null) { _combat.StopChannel(_heldSlot); _heldSlot = null; }
        LastStatus = BehaviorStatus.Failure;
    }
}

/// <summary>
/// Refresh a buff when it's missing or below <paramref name="thresholdSeconds"/>. Aims at
/// self by default.
/// </summary>
public sealed class MaintainBuff : IBehavior
{
    private readonly CombatSystem _combat;
    private readonly Func<BehaviorContext, SkillSlot?> _slotPicker;
    private readonly string _buffName;
    private readonly float _thresholdSeconds;
    private readonly Aim _aim;
    public string Name { get; }
    public BehaviorStatus LastStatus { get; private set; } = BehaviorStatus.Failure;

    public MaintainBuff(string name, CombatSystem combat, Func<BehaviorContext, SkillSlot?> slotPicker, string buffName, float thresholdSeconds = 1.5f, Aim? aim = null)
    {
        Name = name; _combat = combat; _slotPicker = slotPicker; _buffName = buffName;
        _thresholdSeconds = thresholdSeconds;
        _aim = aim ?? Aim.AtSelf();
    }

    public BehaviorStatus Tick(BehaviorContext ctx)
    {
        var player = ctx.Snapshot.Player;
        if (player is null) return LastStatus = BehaviorStatus.Failure;
        var existing = player.Buffs.Find(_buffName);
        if (existing is { } b && b.TimeRemaining > _thresholdSeconds)
            return LastStatus = BehaviorStatus.Success;

        var slot = _slotPicker(ctx);
        if (slot is null) return LastStatus = BehaviorStatus.Failure;
        return LastStatus = _combat.Cast(slot, _aim, ctx, $"refresh {_buffName}");
    }
}
