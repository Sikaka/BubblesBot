using BubblesBot.Bot.Behaviors;
using BubblesBot.Bot.Input;
using BubblesBot.Bot.Settings;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Systems;

/// <summary>
/// Auto-fires flasks. Two modes:
/// <list type="bullet">
/// <item><b>Auto-detect</b> (default): reads the belt via <see cref="FlaskBeltReader"/> and drives
/// each slot by the ACTUAL flask type — life → HP trigger, mana/hybrid → mana trigger, utility →
/// optional timer — pressing that belt slot's key. Follows whatever flasks you actually have,
/// wherever they sit.</item>
/// <item><b>Manual</b>: the explicit per-slot <see cref="FlaskProfile"/> (hotkey + trigger +
/// threshold), used when auto-detect is turned off.</item>
/// </list>
/// <para>HP and Mana are read live from the Life component (see <see cref="LivePlayer"/>). Flasks
/// bypass the click gate (key taps) and stack with combat.</para>
/// </summary>
public sealed class FlaskSystem
{
    private readonly Dictionary<int, TimeSpan> _lastFire = new();

    // Belt contents change rarely — cache and re-read a couple times a second.
    private IReadOnlyList<FlaskBeltReader.Slot> _belt = Array.Empty<FlaskBeltReader.Slot>();
    private TimeSpan _beltReadAt = TimeSpan.MinValue;

    /// <summary>Tick once per behavior tick. Returns true if at least one flask fired.</summary>
    public bool Tick(BehaviorContext ctx)
    {
        if (ctx.Snapshot.Player is not { } player) return false;
        if (ctx.Live is not { } live) return false;
        return ctx.Settings.AutoDetectFlasks ? TickAuto(ctx, live) : TickManual(ctx, live, player);
    }

    public void Reset() => _lastFire.Clear();

    // ── Auto-detect ─────────────────────────────────────────────────────────

    private bool TickAuto(BehaviorContext ctx, LivePlayer live)
    {
        var s = ctx.Settings;
        if (BotMonotonicClock.ElapsedSince(_beltReadAt).TotalSeconds >= 2.0)
        {
            _belt = FlaskBeltReader.Read(ctx.Snapshot.Reader, ctx.Snapshot.IngameDataAddress);
            _beltReadAt = BotMonotonicClock.Now;
        }

        var hpFrac   = live.HpMax   > 0 ? (float)live.HpCurrent   / live.HpMax   : 1f;
        var manaFrac = live.ManaMax > 0 ? (float)live.ManaCurrent / live.ManaMax : 1f;
        var lifeT = s.AutoLifeThresholdPct / 100f;
        var manaT = s.AutoManaThresholdPct / 100f;

        var fired = false;
        foreach (var slot in _belt)
        {
            // Don't waste a keypress on a flask that lacks the charges to fire.
            if (!slot.CanUse) continue;

            bool want;
            int cooldown = s.AutoFlaskCooldownMs;
            switch (slot.Kind)
            {
                case FlaskKind.Life:    want = hpFrac   < lifeT; break;
                case FlaskKind.Mana:    want = manaFrac < manaT; break;
                case FlaskKind.Hybrid:  want = hpFrac < lifeT || manaFrac < manaT; break;
                case FlaskKind.Utility: want = s.AutoUseUtilityFlasks; cooldown = s.AutoUtilityIntervalMs; break;
                default: continue; // Empty
            }
            if (!want) continue;

            var vk = 0x31 + slot.Index; // belt slot 1..5 → keys '1'..'5' (PoE default flask binds)
            if (BotMonotonicClock.ElapsedSince(_lastFire.GetValueOrDefault(vk, TimeSpan.MinValue)).TotalMilliseconds < cooldown)
                continue;

            var ticket = ctx.Input.TapKey(vk, ClickIntent.UseSkill, $"flask slot {slot.Index + 1} ({slot.Kind})");
            if (ticket.Accepted)
            {
                _lastFire[vk] = BotMonotonicClock.Now;
                fired = true;
            }
        }
        return fired;
    }

    // ── Manual per-slot profile ──────────────────────────────────────────────

    private bool TickManual(BehaviorContext ctx, LivePlayer live, PlayerView player)
    {
        var fired = false;
        foreach (var slot in ctx.Settings.Flasks.Slots)
        {
            if (!ShouldFire(slot, live, player)) continue;
            if (!CooldownReady(slot)) continue;
            var ticket = ctx.Input.TapKey(slot.Vk, ClickIntent.UseSkill, $"flask {slot.Name}");
            if (ticket.Accepted)
            {
                _lastFire[slot.Vk] = BotMonotonicClock.Now;
                fired = true;
            }
        }
        return fired;
    }

    private bool CooldownReady(FlaskSlot slot)
    {
        if (!_lastFire.TryGetValue(slot.Vk, out var last)) return true;
        var interval = slot.Trigger == FlaskTrigger.Time ? slot.IntervalMs : slot.CooldownMs;
        return (BotMonotonicClock.Now - last).TotalMilliseconds >= interval;
    }

    private static bool ShouldFire(FlaskSlot slot, LivePlayer live, PlayerView player)
    {
        if (slot.Vk == 0) return false;
        switch (slot.Trigger)
        {
            case FlaskTrigger.Disabled:
                return false;
            case FlaskTrigger.Hp:
                if (live.HpMax <= 0) return false;
                return (float)live.HpCurrent / live.HpMax < slot.HpThreshold;
            case FlaskTrigger.Mana:
                if (live.ManaMax <= 0) return false;
                return (float)live.ManaCurrent / live.ManaMax < slot.ManaThreshold;
            case FlaskTrigger.Time:
                return true; // cooldown gate handles spacing
            case FlaskTrigger.BuffMissing:
                if (string.IsNullOrEmpty(slot.BuffName)) return false;
                return !player.Buffs.Has(slot.BuffName);
        }
        return false;
    }
}
