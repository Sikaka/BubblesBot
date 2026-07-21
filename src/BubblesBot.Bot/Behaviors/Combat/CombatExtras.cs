using BubblesBot.Bot.Settings;
using BubblesBot.Bot.Systems;

namespace BubblesBot.Bot.Behaviors.Combat;

/// <summary>
/// Archetype 4 (primary attacks) as one turnkey leaf. Picks the profile's primary Attack slot
/// and fires it at <paramref name="aim"/>, gated by <see cref="SkillBook"/> readiness:
/// <list type="bullet">
///   <item>default — a single tap per ready tick (the existing drive-by behavior);</item>
///   <item><see cref="SkillSlot.HoldToRepeat"/> — hold the key down and retarget the cursor
///         each tick, for builds whose primary damage is "hold to attack" (auto-repeat).</item>
/// </list>
/// A slot with <see cref="SkillSlot.LockMs"/> &gt; 0 stamps the shared <see cref="ActionState"/>
/// (via <see cref="CombatSystem"/>) when it fires, so movement/dodge can see the attack-lock.
///
/// <para><b>Staged:</b> compiles; not yet substituted into the validated combat modes (they use
/// the plain <c>Cast</c> tap today). Wire + validate against a real build next round.</para>
/// </summary>
public sealed class PrimaryAttack : IBehavior
{
    private readonly CombatSystem _combat;
    private readonly Func<BehaviorContext, SkillSlot?> _slotPicker;
    private readonly Aim _aim;
    private readonly SkillBook? _book;
    private SkillSlot? _held;

    public string Name { get; }
    public BehaviorStatus LastStatus { get; private set; } = BehaviorStatus.Failure;

    public PrimaryAttack(string name, CombatSystem combat, Aim aim, SkillBook? book = null,
        Func<BehaviorContext, SkillSlot?>? slotPicker = null)
    {
        Name = name;
        _combat = combat;
        _aim = aim;
        _book = book;
        _slotPicker = slotPicker ?? (ctx => ctx.Settings.Skills.PrimaryAttack);
    }

    public BehaviorStatus Tick(BehaviorContext ctx)
    {
        var slot = _slotPicker(ctx);
        if (slot is null) { StopHeld(); return LastStatus = BehaviorStatus.Failure; }

        if (slot.HoldToRepeat)
        {
            // Hold-to-attack: keep the key down + retarget the aim. Releasing is driven by the
            // parent (a Reset when the tree stops choosing to attack).
            _held = slot;
            return LastStatus = _combat.HoldChannel(slot, _aim, ctx);
        }

        StopHeld();
        if (_book is not null && !_book.IsReady(slot)) return LastStatus = BehaviorStatus.Failure;
        var result = _combat.Cast(slot, _aim, ctx, Name);
        if (result == BehaviorStatus.Success) _book?.MarkCast(slot);
        return LastStatus = result;
    }

    private void StopHeld()
    {
        if (_held is not null) { _combat.StopChannel(_held); _held = null; }
    }

    public void Reset() { StopHeld(); LastStatus = BehaviorStatus.Failure; }
}

/// <summary>
/// Archetype 3 (castable single-use) for self-buffs and auras. Each tick, fires the first ready
/// <see cref="SkillRole.SelfBuff"/> slot at self (SkillBook-gated interval recast), and toggles
/// each <see cref="SkillRole.Aura"/> slot on exactly once per activation (auras persist, so we
/// don't re-tap them until <see cref="Reset"/>). Always returns Success (it's a maintenance leaf
/// that runs in parallel, never blocks the tree).
///
/// <para><b>Buff-uptime gating deferred:</b> "don't recast while the buff is still up / refresh
/// before it expires" needs validated Buffs offsets — use <see cref="MaintainBuff"/> for that
/// once <c>BuffsView</c> is confirmed. This leaf covers the interval/once-off cases that don't
/// require reading buff state.</para>
///
/// <para><b>Staged:</b> compiles; not yet wired into a mode.</para>
/// </summary>
public sealed class MaintainSelfBuffs : IBehavior
{
    private readonly CombatSystem _combat;
    private readonly SkillBook? _book;
    private readonly HashSet<SkillSlot> _aurasFired = new();

    public string Name { get; }
    public BehaviorStatus LastStatus { get; private set; } = BehaviorStatus.Success;

    public MaintainSelfBuffs(string name, CombatSystem combat, SkillBook? book = null)
    {
        Name = name; _combat = combat; _book = book;
    }

    public BehaviorStatus Tick(BehaviorContext ctx)
    {
        // Auras: fire each once per activation window.
        foreach (var aura in ctx.Settings.Skills.OfRole(SkillRole.Aura))
        {
            if (_aurasFired.Contains(aura)) continue;
            if (_book is not null && !_book.IsReady(aura)) continue;
            if (_combat.Cast(aura, Aim.AtSelf(), ctx, $"aura {aura.Name}") == BehaviorStatus.Success)
            {
                _book?.MarkCast(aura);
                _aurasFired.Add(aura);
            }
        }

        // Self-buffs: recast the first ready one this tick (SkillBook interval spacing applies).
        foreach (var buff in ctx.Settings.Skills.OfRole(SkillRole.SelfBuff))
        {
            if (_book is not null && !_book.IsReady(buff)) continue;
            if (_combat.Cast(buff, Aim.AtSelf(), ctx, $"buff {buff.Name}") == BehaviorStatus.Success)
            {
                _book?.MarkCast(buff);
                break; // one buff per tick — don't machine-gun the whole bar in one frame
            }
        }

        return LastStatus = BehaviorStatus.Success;
    }

    public void Reset()
    {
        _aurasFired.Clear(); // re-toggle auras after an interruption / area change
        LastStatus = BehaviorStatus.Success;
    }
}

/// <summary>
/// Opportunistically applies the configured targeted mark to the highest-priority rare or
/// unique enemy in range. This is a maintenance leaf: it never blocks the owning combat
/// posture, and <see cref="SkillBook"/> supplies the per-profile recast interval.
/// </summary>
public sealed class MaintainTargetedMark : IBehavior
{
    private readonly CombatSystem _combat;
    private readonly SkillBook? _book;

    public string Name { get; }
    public BehaviorStatus LastStatus { get; private set; } = BehaviorStatus.Success;

    public MaintainTargetedMark(string name, CombatSystem combat, SkillBook? book = null)
    {
        Name = name;
        _combat = combat;
        _book = book;
    }

    public BehaviorStatus Tick(BehaviorContext ctx)
    {
        var mark = ctx.Settings.Skills.PrimaryMark;
        if (mark is null || (_book is not null && !_book.IsReady(mark)))
            return LastStatus = BehaviorStatus.Success;

        var range = mark.MaxRangeGrid > 0 ? mark.MaxRangeGrid : 60f;
        var target = Threat.Biggest(ctx, range, requireLos: false);
        if (target is null || Threat.RarityRank(target) < 2)
            return LastStatus = BehaviorStatus.Success;

        if (_combat.Cast(mark, Aim.AtEntity(target.Id), ctx, $"mark {mark.Name}")
            == BehaviorStatus.Success)
            _book?.MarkCast(mark);

        return LastStatus = BehaviorStatus.Success;
    }

    public void Reset() => LastStatus = BehaviorStatus.Success;
}
