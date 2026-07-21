using BubblesBot.Bot.Settings;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Systems;

/// <summary>
/// Per-slot runtime state. Tracks how many uses are stockpiled (charges) and the timestamp of
/// the last fire, so the rest of the bot can ask "is this slot ready right now, and if not
/// when will the next charge arrive?"
///
/// <para><b>v1 model: client-side simulation.</b> No memory reads. Charges are decremented on
/// <see cref="MarkCast"/> and refilled at <see cref="SkillSlot.ChargeRechargeMs"/> per charge
/// up to <see cref="SkillSlot.ChargeCount"/>. <see cref="SkillSlot.MinCastIntervalMs"/> acts
/// as an additional floor between consecutive casts. This is wrong in the cases where PoE
/// recovers charges differently (cooldown reduction stats, recharge bonuses) but it's the
/// right shape for the eventual real <c>ActorSkillCooldown</c>-backed implementation —
/// callers won't change.</para>
///
/// <para>Lookup by <see cref="SkillSlot"/> reference. The profile's <c>Slots</c> list is
/// shared by reference between settings and SkillBook, so identity-based dictionary keys
/// stay correct across config edits unless the user replaces a slot wholesale.</para>
/// </summary>
public sealed class SkillBook
{
    private readonly Dictionary<SkillSlot, State> _state = new();

    /// <summary>
    /// When set, IsReady consults <see cref="SkillCooldownReader"/> for slots whose
    /// <see cref="SkillSlot.GemId"/> is non-zero. Set once at bot startup and pair with
    /// <see cref="SetActorContext"/> each tick so we know where the actor lives.
    /// </summary>
    public SkillCooldownReader? CooldownReader { get; set; }
    private nint _actorComponentAddress;

    /// <summary>Update the live actor address each tick. Called from the active mode's Tick.</summary>
    public void SetActorContext(nint actorComponentAddress) => _actorComponentAddress = actorComponentAddress;

    /// <summary>Charges currently available for the slot. Caller-side simulation.</summary>
    public int ChargesReady(SkillSlot slot)
    {
        var s = StateOf(slot);
        Refill(slot, s, BotMonotonicClock.Now);
        return s.Charges;
    }

    /// <summary>
    /// True when the slot can fire right now. Layered check:
    /// <list type="number">
    ///   <item><b>Min-cast-interval floor</b> (always applied) — defends against memory not
    ///         updating yet after a recent cast, and rate-limits mash-fire of skills with
    ///         no in-game cooldown.</item>
    ///   <item><b>Real cooldown read</b> when <see cref="SkillSlot.GemId"/> is set AND the
    ///         reader is wired — authoritative.</item>
    ///   <item><b>Client-side simulation</b> as the fallback.</item>
    /// </list>
    /// </summary>
    public bool IsReady(SkillSlot slot)
    {
        var s = StateOf(slot);
        var now = BotMonotonicClock.Now;

        // Always enforce the min-cast-interval floor — even when the in-game skill has no
        // cooldown, this prevents the bot from triple-tapping during the 1-2 ticks before
        // memory shows the cast.
        if (slot.MinCastIntervalMs > 0
            && BotMonotonicClock.ElapsedSince(s.LastCastAt).TotalMilliseconds < slot.MinCastIntervalMs)
            return false;

        // Real-memory cooldown read takes precedence when wired.
        if (slot.GemId != 0 && CooldownReader is not null && _actorComponentAddress != 0)
        {
            var live = CooldownReader.Read(_actorComponentAddress, slot.GemId);
            if (live is { } cs) return cs.IsReady;
            // null = skill has no cooldown entry (e.g. basic attack) → fall through to sim.
        }

        Refill(slot, s, now);
        return s.Charges >= 1;
    }

    /// <summary>
    /// Estimated milliseconds until the slot has at least <paramref name="needed"/> charges.
    /// 0 if already satisfied. Used by A* to budget how many blinks fit in the path's time
    /// horizon.
    /// </summary>
    public int MillisUntilCharges(SkillSlot slot, int needed)
    {
        if (needed <= 0) return 0;
        var s = StateOf(slot);
        var now = BotMonotonicClock.Now;
        Refill(slot, s, now);
        if (s.Charges >= needed) return 0;
        var deficit = needed - s.Charges;
        // We have <needed charges; next charge lands at lastRefillAt + recharge, then every
        // recharge_ms after that. Compute time-of-next then add (deficit-1) more recharges.
        var msToNext = Math.Max(0, slot.ChargeRechargeMs - (int)(now - s.LastRefillAt).TotalMilliseconds);
        return msToNext + (deficit - 1) * slot.ChargeRechargeMs;
    }

    /// <summary>Record that the slot was just fired. Decrements charges, sets last-cast clock.</summary>
    public void MarkCast(SkillSlot slot)
    {
        var s = StateOf(slot);
        var now = BotMonotonicClock.Now;
        Refill(slot, s, now);
        if (s.Charges > 0) s.Charges--;
        s.LastCastAt = now;
    }

    /// <summary>Pick the first ready slot in <paramref name="candidates"/>, or null.</summary>
    public SkillSlot? PickReady(IEnumerable<SkillSlot> candidates)
    {
        foreach (var s in candidates) if (IsReady(s)) return s;
        return null;
    }

    /// <summary>Reset all per-slot state. Use on area transition (charges reset client-side too).</summary>
    public void Reset()
    {
        var now = BotMonotonicClock.Now;
        foreach (var (slot, st) in _state)
        {
            st.Charges = slot.ChargeCount;
            st.LastCastAt = TimeSpan.MinValue;
            st.LastRefillAt = now;
        }
    }

    private State StateOf(SkillSlot slot)
    {
        if (_state.TryGetValue(slot, out var s)) return s;
        s = new State { Charges = slot.ChargeCount, LastRefillAt = BotMonotonicClock.Now };
        _state[slot] = s;
        return s;
    }

    private static void Refill(SkillSlot slot, State s, TimeSpan now)
    {
        if (s.Charges >= slot.ChargeCount) { s.LastRefillAt = now; return; }
        if (slot.ChargeRechargeMs <= 0) return;
        var elapsed = (now - s.LastRefillAt).TotalMilliseconds;
        var gain = (int)(elapsed / slot.ChargeRechargeMs);
        if (gain <= 0) return;
        s.Charges = Math.Min(slot.ChargeCount, s.Charges + gain);
        s.LastRefillAt += TimeSpan.FromMilliseconds(gain * (double)slot.ChargeRechargeMs);
    }

    private sealed class State
    {
        public int      Charges;
        public TimeSpan LastCastAt   = TimeSpan.MinValue;
        public TimeSpan LastRefillAt = BotMonotonicClock.Now;
    }
}
