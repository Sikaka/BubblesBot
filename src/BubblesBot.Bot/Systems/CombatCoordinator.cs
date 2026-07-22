using BubblesBot.Bot.Behaviors;
using BubblesBot.Bot.Behaviors.Movement;
using BubblesBot.Bot.Behaviors.Combat;
using BubblesBot.Bot.Input;
using BubblesBot.Bot.Settings;
using BubblesBot.Core.Game;
using BubblesBot.Core.Knowledge;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Systems;

/// <summary>
/// Global combat brain shared by every general-combat mode (map farming, Simulacrum). It owns
/// the combat systems (one instance — only one mode ticks per frame) and the tactical
/// primitives: target selection, drive-by/proximity engage, unload-on-rare, low-HP retreat,
/// the can't-hit damage-evidence blacklist, Righteous-Fire re-light/douse, and Penance-Mark
/// (curse) casting. Modes compose these with their own exploration/lifecycle; policy that is
/// NOT combat (disarm, abandon-map) is surfaced through <see cref="PostRoot"/>'s result rather
/// than owned here.
///
/// <para>Blight is deliberately NOT a client — it is pump/tower defence, not general combat.</para>
///
/// <para>The RF re-light/douse block carries hard-won live-incident fixes; preserve them:
/// the 80&#160;ms <see cref="IInputRouter.BeginHoldKey"/> pulse (PoE drops zero-duration RF
/// taps), the re-light HP floor sitting ABOVE the douse line (avoids a burn↔recover loop),
/// <see cref="BotMonotonicClock.ElapsedSince"/> instead of raw <c>MinValue</c> subtraction
/// (an OverflowException disarmed mid-fight once), buff-read flicker debounce, and the distinct
/// evidence windows (proximity ≈4000&#160;ms vs attack ≥1200&#160;ms).</para>
/// </summary>
public sealed class CombatCoordinator
{
    private const int    RetreatStepGrid    = 24;
    private const double ToughTargetAttackWindowMs = 850;
    private const float  DefaultAttackRange = 55f;
    private const double BlacklistHoldMs    = 15000;
    private const int    RequiredBuffPulseMs = 80;
    private const double ProximityDamageEvidenceMs = 4000;
    private const int    DouseAfterLowHpSeconds = 5;
    private const int    MaxDouseAttempts = 3;
    private const float  RequiredBuffCombatRangeGrid = 60f;

    public MovementSystem Movement { get; }
    public CombatSystem   Combat  { get; } = new();
    public SkillBook      Skills  { get; } = new();
    public FlaskSystem    Flasks  { get; } = new();

    private readonly DamageEvidenceTracker _damageEvidence = new();
    private readonly FollowPath _engageFollow;
    private readonly FollowPath _flickerFollow;
    private readonly FollowPath _rangedFollow;
    private readonly Reposition _rangedReposition;
    private readonly MaintainSelfBuffs _selfBuffs;
    private bool _engagePreferDensity;
    private Func<EntityCache.Entry, bool>? _engageAdditionalSkip;
    private Func<EntityCache.Entry, bool>? _rangedAdditionalSkip;
    private EntityCache.Entry? _flickerTarget;

    // per-tick flags (reset in BeginTick)
    private bool _qHeldThisTick;
    private bool _proximityEvidenceThisTick;
    private bool _attackIssuedThisTick;
    private uint _attackIssuedTargetId;
    private double _attackEvidenceDelayMs = 1200;
    private TimeSpan _rangedToughExposureAt = TimeSpan.MinValue;

    // RF re-light pulse state
    private TimeSpan _requiredBuffLastCastAt = TimeSpan.Zero;
    private int _requiredBuffAttempts;
    private IInputHandle? _requiredBuffPulse;
    private TimeSpan _requiredBuffPulseStartedAt;

    // Emergency douse state
    private TimeSpan? _buffLowHpSince;
    private TimeSpan _douseLastAt = TimeSpan.MinValue;
    private int _douseAttempts;
    private IInputHandle? _dousePulse;
    private TimeSpan _dousePulseStartedAt;

    private string? _rfFatalReason;

    public CombatCoordinator(MovementSystem movement)
    {
        Movement = movement;
        _engageFollow = new FollowPath("engage", Movement,
            ctx => SelectProximityTarget(ctx, _engagePreferDensity, _engageAdditionalSkip)?.GridPosition,
            Skills,
            goalArrivalRadiusProvider: ctx => ctx.Settings.ProximityHoldRadiusGrid);
        _flickerFollow = new FollowPath("flicker engage", Movement,
            _ => _flickerTarget?.GridPosition,
            Skills,
            goalArrivalRadiusProvider: AttackRange,
            preferDashForLongTravel: true);
        _rangedFollow = new FollowPath("ranged engage", Movement,
            ctx => SelectRangedTarget(ctx, _rangedAdditionalSkip)?.GridPosition,
            Skills,
            goalArrivalRadiusProvider: ctx => Math.Min(
                Math.Max(10f, ctx.Settings.RangedStandoffGrid), AttackRange(ctx)),
            preferDashForLongTravel: true);
        _rangedReposition = new Reposition(
            "ranged kite", Movement, Skills,
            ctx => RetreatPoint(ctx, allowGapCrossing: true,
                Math.Max(35f, ctx.Settings.RangedStandoffGrid)));
        _selfBuffs = new MaintainSelfBuffs("combat self buffs", Combat, Skills);
    }

    // ── telemetry passthrough ───────────────────────────────────────────────
    public uint EngagedId => _damageEvidence.EngagedId;
    public bool RangedRepositionActive => _rangedReposition.IsActive;
    public int  BlacklistCount => _damageEvidence.BlacklistCount;
    public double EngagedForMs(TimeSpan now) => _damageEvidence.EngagedForMs(now);
    public IEnumerable<(uint Id, double RemainingMs)> Blacklist(TimeSpan now) => _damageEvidence.Blacklist(now);
    public string? RfFatalReason => _rfFatalReason;

    /// <summary>Result of the post-root maintenance pass — combat surfaces policy the mode owns.</summary>
    public readonly record struct CombatTickResult(bool DouseConfirmed, string? FatalReason);

    // ── per-tick lifecycle ──────────────────────────────────────────────────

    /// <summary>Wire actor context + cooldown reader and reset per-tick flags. Call first.</summary>
    public void BeginTick(GameSnapshot snapshot)
    {
        if (snapshot.Player is { } pv) Skills.SetActorContext(pv.ActorComponentAddress);
        if (Skills.CooldownReader is null) Skills.CooldownReader = new SkillCooldownReader(snapshot.Reader);
        _qHeldThisTick = false;
        _proximityEvidenceThisTick = false;
        _attackIssuedThisTick = false;
    }

    /// <summary>Flasks + RF/douse pulse advance. Call before the mode's behavior tree ticks.</summary>
    public void PreRoot(BehaviorContext ctx)
    {
        Flasks.Tick(ctx);
        _selfBuffs.Tick(ctx);
        AdvanceRequiredBuffPulse(ctx);
        AdvanceDousePulse();
    }

    /// <summary>
    /// Post-tree maintenance: confirm RF re-light/douse against live buffs, run damage-evidence,
    /// and release a stale held attack. Returns the douse-confirmed edge (mode counts abandons)
    /// and any fatal RF misconfiguration (mode disarms/stops).
    /// </summary>
    public CombatTickResult PostRoot(BehaviorContext ctx)
    {
        var snapshot = ctx.Snapshot;
        var buff = ctx.Settings.RequiredMapBuffName.Trim();
        var douseConfirmed = false;

        if (_requiredBuffAttempts > 0 && buff.Length > 0
            && snapshot.Player is { } buffPlayer && buffPlayer.Buffs.Has(buff))
        {
            Diagnostics.EventLog.Emit(
                "combat", "combat.required-buff-confirmed", Diagnostics.EventSeverity.Info,
                $"confirmed required map buff '{buff}'");
            _requiredBuffAttempts = 0;
        }
        if (_douseAttempts > 0 && buff.Length > 0
            && snapshot.Player is { } dousedPlayer && !dousedPlayer.Buffs.Has(buff))
        {
            Diagnostics.EventLog.Emit(
                "combat", "combat.required-buff-doused", Diagnostics.EventSeverity.Info,
                $"'{buff}' is off; recovering before re-light");
            _douseAttempts = 0;
            _buffLowHpSince = null;
            _requiredBuffAttempts = 0; // fresh activation budget for the eventual re-light
            douseConfirmed = true;
        }

        UpdateEngagement(ctx);

        // Release the held attack whenever we're not unloading, so a leftover hold can't suppress
        // the next tap (TapKey refuses a currently-held vk).
        if (!_qHeldThisTick)
        {
            var q = PickAttackSlot(ctx.Settings);
            if (q is not null) Combat.StopChannel(q);
        }

        return new CombatTickResult(douseConfirmed, _rfFatalReason);
    }

    /// <summary>Clear all combat state (area change / mode switch). Modes call from their Reset.</summary>
    public void ResetCombat()
    {
        Combat.StopAllChannels();
        Skills.Reset();
        Flasks.Reset();
        _engageFollow.Reset();
        _flickerFollow.Reset();
        _flickerTarget = null;
        _rangedFollow.Reset();
        _rangedReposition.Reset();
        _selfBuffs.Reset();
        _damageEvidence.Reset();
        _requiredBuffAttempts = 0;
        _requiredBuffLastCastAt = TimeSpan.Zero;
        _requiredBuffPulse?.Release();
        _requiredBuffPulse = null;
        _requiredBuffPulseStartedAt = TimeSpan.Zero;
        _buffLowHpSince = null;
        _douseLastAt = TimeSpan.MinValue;
        _douseAttempts = 0;
        _dousePulse?.Release();
        _dousePulse = null;
        _qHeldThisTick = false;
        _proximityEvidenceThisTick = false;
        _attackIssuedThisTick = false;
        _attackIssuedTargetId = 0;
        _rangedToughExposureAt = TimeSpan.MinValue;
        _engagePreferDensity = false;
        _engageAdditionalSkip = null;
        _rangedAdditionalSkip = null;
        _rfFatalReason = null;
    }

    /// <summary>Clear the per-map RF-pulse retry budget on an area change without a full reset.</summary>
    public void OnAreaChanged()
    {
        _requiredBuffAttempts = 0;
        _requiredBuffLastCastAt = TimeSpan.Zero;
        _requiredBuffPulse?.Release();
        _requiredBuffPulse = null;
        _requiredBuffPulseStartedAt = TimeSpan.Zero;
        _selfBuffs.Reset();
        _rangedFollow.Reset();
        _rangedReposition.Reset();
        _rangedToughExposureAt = TimeSpan.MinValue;
    }

    // ── Postures ──────────────────────────────────────────────────────────

    /// <summary>Opportunistic tap at the biggest in-range threat (LOS not required).</summary>
    public void TapBiggestThreat(BehaviorContext ctx)
    {
        var target = Engageable(ctx, AttackRange(ctx), requireLos: false);
        if (target is null) return;
        TapThreat(ctx, target, "push tap");
    }

    public void TapThreat(BehaviorContext ctx, EntityCache.Entry target, string intent)
    {
        var q = PickAttackSlot(ctx.Settings);
        if (q is null || !Skills.IsReady(q)) return;
        if (ctx.Live is { } live
            && Distance(live.GridPosition, target.GridPosition) > AttackRange(ctx)) return;
        if (Combat.Cast(q, Aim.AtEntity(target.Id), ctx, intent) == BehaviorStatus.Success)
        {
            Skills.MarkCast(q);
            MarkAttackIssued(target.Id, q);
        }
    }

    /// <summary>Low HP: stop attacking and create separation, preferring a ready dash.</summary>
    public BehaviorStatus RetreatTick(BehaviorContext ctx)
    {
        var attack = PickAttackSlot(ctx.Settings);
        if (attack is not null)
            Combat.StopChannel(attack);

        if (ctx.Settings.MapClearStance == 2
            && (_rangedReposition.IsActive
                || Skills.PickReady(ctx.Settings.Skills.OfRole(SkillRole.Dash)) is not null))
        {
            var reposition = _rangedReposition.Tick(ctx);
            if (reposition != BehaviorStatus.Failure)
                return BehaviorStatus.Running;
        }

        // A ranged build does not spend the entire Blink Arrow recharge window doing zero
        // damage. Once the dash has created standoff, hold the primary attack to leech and kill;
        // only keep walking when an eligible threat is still too close or cannot be shot. The
        // next ready dash preempts this branch above, producing the intended W -> Q -> W chain.
        if (ctx.Settings.MapClearStance == 2
            && attack is not null
            && TryAttackFromSafeRetreatStandoff(ctx, attack))
            return BehaviorStatus.Running;

        var away = RetreatPoint(ctx, allowGapCrossing: false, RetreatStepGrid);
        if (away is { } a) Movement.WalkToward(a, new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live));
        else Movement.Halt(new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live));
        return BehaviorStatus.Running;
    }

    /// <summary>Rare/Unique in LOS range: stand and hold the attack for max DPS.</summary>
    public BehaviorStatus UnloadTick(BehaviorContext ctx)
    {
        var q = PickAttackSlot(ctx.Settings);
        var target = Engageable(ctx, AttackRange(ctx), requireLos: true);
        if (q is null || target is null) return BehaviorStatus.Failure;
        Movement.Halt(new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live));
        var result = Combat.HoldChannel(q, Aim.AtEntity(target.Id), ctx);
        if (result == BehaviorStatus.Running) MarkAttackIssued(target.Id, q);
        _qHeldThisTick = true;
        return BehaviorStatus.Running;
    }

    /// <summary>
    /// Flicker Strike is both the build's damage and its movement/defence loop. Route to the
    /// nearest active hostile, then hold the skill continuously so it can teleport through
    /// the pack. This intentionally owns low-HP ticks too; generic retreat stops Flicker and
    /// leaves the character standing among an add wave without leech or damage.
    /// </summary>
    public BehaviorStatus FlickerEngageTick(BehaviorContext ctx)
    {
        var attack = PickAttackSlot(ctx.Settings);
        _flickerTarget = SelectFlickerTarget(ctx);
        if (!IsFlickerStrike(attack) || _flickerTarget is null || ctx.Live is not { } live)
        {
            if (attack is not null) Combat.StopChannel(attack);
            _flickerFollow.Reset();
            return BehaviorStatus.Failure;
        }

        if (Distance(live.GridPosition, _flickerTarget.GridPosition) > AttackRange(ctx))
        {
            Combat.StopChannel(attack!);
            _flickerFollow.Tick(ctx);
            return BehaviorStatus.Running;
        }

        _flickerFollow.Reset();
        Movement.Halt(new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live));
        var result = Combat.HoldChannel(attack!, Aim.AtEntity(_flickerTarget.Id), ctx);
        if (result == BehaviorStatus.Running)
        {
            _qHeldThisTick = true;
            MarkAttackIssued(_flickerTarget.Id, attack!);
        }
        return BehaviorStatus.Running;
    }

    public EntityCache.Entry? SelectFlickerTarget(BehaviorContext ctx)
    {
        if (!IsFlickerStrike(PickAttackSlot(ctx.Settings))) return null;
        var seekRange = Math.Max(220f, ctx.Settings.CombatEngageRange);
        // Keep the current pursuit stable. Re-selecting the Euclidean-nearest hostile on
        // every frame lets two packs on opposite sides of the character alternate ownership
        // as it moves, producing a permanent back-and-forth route. A committed target remains
        // ours until it actually dies/disappears, is blacklisted, or leaves the seek radius.
        if (_flickerTarget is { } previous
            && ctx.Entities?.Entries.TryGetValue(previous.Id, out var locked) == true
            && TargetEligibility.IsEligible(locked)
            && !IsBlacklisted(locked.Id)
            && ctx.Live is { } live
            && Distance(live.GridPosition, locked.GridPosition) <= seekRange)
            return locked;

        _flickerTarget = null;
        // Long-range Euclidean selection can see a monster through an entire wall section.
        // That lets combat preempt exploration forever while the approach repeatedly tries
        // to Leap Slam through terrain. Exploration owns routing around occlusion; Flicker
        // takes over as soon as a clear chain target is exposed.
        return _flickerTarget = Threat.Nearest(
            ctx, seekRange, skip: IsBlacklisted, requireLos: true);
    }

    /// <summary>
    /// Proximity engage: path to the selected pack and stand among it while auras/RF/triggered
    /// spells kill. Within the hold radius we halt; beyond it we walk in. Returns Running while a
    /// hostile is engageable so the caller stays parked here, Failure when nothing to engage.
    /// </summary>
    public BehaviorStatus ProximityEngageTick(
        BehaviorContext ctx,
        bool preferDensity = false,
        Func<EntityCache.Entry, bool>? additionalSkip = null)
    {
        _engagePreferDensity = preferDensity;
        _engageAdditionalSkip = additionalSkip;
        var target = SelectProximityTarget(ctx, preferDensity, additionalSkip);
        if (target is null || ctx.Live is null)
        {
            _damageEvidence.ClearEngagement();
            return BehaviorStatus.Failure;
        }
        var p = ctx.Live.Value.GridPosition;
        float dx = target.GridPosition.X - p.X, dy = target.GridPosition.Y - p.Y;
        if (MathF.Sqrt(dx * dx + dy * dy) <= ctx.Settings.ProximityHoldRadiusGrid)
        {
            Movement.Halt(new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live));
            var now = BotMonotonicClock.Now;
            _proximityEvidenceThisTick = true;
            var evidence = _damageEvidence.ObserveAcceptedAttack(
                target.Id, target.HpCurrent, now,
                ProximityDamageEvidenceMs, BlacklistHoldMs);
            if (evidence.Outcome == DamageEvidenceOutcome.Blacklisted)
            {
                Diagnostics.EventLog.Emit(
                    "combat", "combat.proximity-no-damage", Diagnostics.EventSeverity.Warning,
                    $"blacklisted proximity target {target.Id} after " +
                    $"{evidence.ElapsedMs:F0}ms with no HP change",
                    new Dictionary<string, object?>
                    {
                        ["targetId"] = target.Id,
                        ["path"] = target.Path,
                        ["hp"] = target.HpCurrent,
                        ["evidenceMs"] = evidence.ElapsedMs,
                        ["holdMs"] = BlacklistHoldMs,
                    });
            }
        }
        else
        {
            _damageEvidence.ClearEngagement();
            _engageFollow.Tick(ctx);
        }
        return BehaviorStatus.Running;
    }

    /// <summary>
    /// Ranged engage: close only until the configured standoff has a walkable line of fire,
    /// then stop and hold an auto-repeat primary attack at the selected threat. The dedicated
    /// approach path may spend a ready Dash on long legs; ordinary exploration remains walk-first.
    /// </summary>
    public BehaviorStatus RangedEngageTick(
        BehaviorContext ctx,
        Func<EntityCache.Entry, bool>? additionalSkip = null)
    {
        _rangedAdditionalSkip = additionalSkip;
        var attack = PickAttackSlot(ctx.Settings);
        if (attack is null || ctx.Live is not { } live)
        {
            if (attack is not null) Combat.StopChannel(attack);
            _rangedFollow.Reset();
            _rangedReposition.Reset();
            _damageEvidence.ClearEngagement();
            return BehaviorStatus.Failure;
        }

        // Once W has fired, this branch must retain ownership until observed displacement or
        // timeout. If target selection flickers for one frame and exploration takes over, its
        // ordinary walking can otherwise be mistaken for a delayed Blink Arrow landing.
        if (_rangedReposition.IsActive)
        {
            Combat.StopChannel(attack);
            _rangedFollow.Reset();
            _damageEvidence.ClearEngagement();
            var reposition = _rangedReposition.Tick(ctx);
            if (reposition != BehaviorStatus.Failure)
                return BehaviorStatus.Running;
        }

        var target = SelectRangedTarget(ctx, additionalSkip);
        if (target is null)
        {
            Combat.StopChannel(attack);
            _rangedFollow.Reset();
            _damageEvidence.ClearEngagement();
            return BehaviorStatus.Failure;
        }

        var distance = Distance(live.GridPosition, target.GridPosition);
        var stopAt = Math.Min(Math.Max(10f, ctx.Settings.RangedStandoffGrid), AttackRange(ctx));
        var hasLineOfFire = ctx.Snapshot.Nav.PathReader is not { } path
            || BubblesBot.Core.Pathfinding.PathSmoother.HasLineOfSight(
                path, live.GridPosition.X, live.GridPosition.Y,
                target.GridPosition.X, target.GridPosition.Y, minValue: 1);

        if (TryRangedReposition(ctx, attack))
            return BehaviorStatus.Running;

        if (distance <= stopAt && hasLineOfFire)
        {
            _rangedFollow.Reset();
            Movement.Halt(new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live));
            BehaviorStatus result;
            if (attack.HoldToRepeat)
            {
                result = Combat.HoldChannel(attack, Aim.AtEntity(target.Id), ctx);
                _qHeldThisTick = result == BehaviorStatus.Running;
            }
            else if (Skills.IsReady(attack))
            {
                result = Combat.Cast(attack, Aim.AtEntity(target.Id), ctx, "ranged attack");
                if (result == BehaviorStatus.Success) Skills.MarkCast(attack);
            }
            else
            {
                result = BehaviorStatus.Running;
            }

            if (result is BehaviorStatus.Running or BehaviorStatus.Success)
                MarkAttackIssued(target.Id, attack);
            return BehaviorStatus.Running;
        }

        Combat.StopChannel(attack);
        _damageEvidence.ClearEngagement();
        _rangedFollow.Tick(ctx);
        return BehaviorStatus.Running;
    }

    private bool TryRangedReposition(BehaviorContext ctx, SkillSlot attack)
    {
        if (ctx.Live is not { } live)
            return false;

        var hpFraction = live.HpMax > 0 ? (float)live.HpCurrent / live.HpMax : 1f;
        var healthPressure = ctx.Settings.RangedKiteHpPercent > 0
            && hpFraction < ctx.Settings.RangedKiteHpPercent / 100f;

        var toughRange = Math.Max(ctx.Settings.CombatEngageRange, AttackRange(ctx) + 30f);
        var tough = ctx.Settings.RangedKiteToughTargets
            ? Threat.Biggest(ctx, toughRange, requireLos: false, skip: IsBlacklisted)
            : null;
        var toughPressure = tough is not null && Threat.IsToughTarget(tough);
        var now = BotMonotonicClock.Now;
        if (!toughPressure)
            _rangedToughExposureAt = TimeSpan.MinValue;
        else if (_rangedToughExposureAt == TimeSpan.MinValue)
            _rangedToughExposureAt = now;

        var toughWindowElapsed = toughPressure
            && BotMonotonicClock.ElapsedSince(_rangedToughExposureAt).TotalMilliseconds
                >= ToughTargetAttackWindowMs;
        var dashReady = Skills.PickReady(ctx.Settings.Skills.OfRole(SkillRole.Dash)) is not null;
        var shouldDash = _rangedReposition.IsActive
            || ((healthPressure || toughWindowElapsed) && dashReady);

        if (shouldDash)
        {
            Combat.StopChannel(attack);
            _rangedFollow.Reset();
            _damageEvidence.ClearEngagement();
            var result = _rangedReposition.Tick(ctx);
            if (result != BehaviorStatus.Failure)
            {
                if (toughWindowElapsed)
                    _rangedToughExposureAt = now;
                return true;
            }
        }

        // A meaningful hit still owns the posture while Blink Arrow is recharging.
        if (healthPressure)
        {
            Combat.StopChannel(attack);
            _rangedFollow.Reset();
            var away = RetreatPoint(ctx, allowGapCrossing: false, RetreatStepGrid);
            if (away is { } point)
                Movement.WalkToward(point, new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live));
            else
                Movement.Halt(new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live));
            return true;
        }

        return false;
    }

    private bool TryAttackFromSafeRetreatStandoff(BehaviorContext ctx, SkillSlot attack)
    {
        if (ctx.Live is not { } live)
            return false;

        var target = Threat.Nearest(ctx, AttackRange(ctx), skip: IsBlacklisted);
        if (target is null)
            return false;

        var distance = Distance(live.GridPosition, target.GridPosition);
        if (!RangedRetreatPolicy.ShouldAttackWhileDashRecharges(
                distance, AttackRange(ctx), ctx.Settings.RangedStandoffGrid))
            return false;

        var path = ctx.Snapshot.Nav.PathReader;
        if (path is not null
            && !BubblesBot.Core.Pathfinding.PathSmoother.HasLineOfSight(
                path, live.GridPosition.X, live.GridPosition.Y,
                target.GridPosition.X, target.GridPosition.Y, minValue: 1))
            return false;

        Movement.Halt(new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live));
        var result = Combat.HoldChannel(attack, Aim.AtEntity(target.Id), ctx);
        if (result == BehaviorStatus.Running)
        {
            _qHeldThisTick = true;
            MarkAttackIssued(target.Id, attack);
        }
        return result is BehaviorStatus.Running or BehaviorStatus.Success;
    }

    public EntityCache.Entry? SelectRangedTarget(
        BehaviorContext ctx,
        Func<EntityCache.Entry, bool>? additionalSkip = null)
    {
        var range = Math.Max(ctx.Settings.CombatEngageRange, AttackRange(ctx) + 30f);
        // Nearest-first is the safe ranged policy: choosing a farther rare/unique can route
        // straight through ordinary monsters that are already threatening the character.
        return Threat.Nearest(ctx, range,
            skip: id => IsBlacklisted(id)
                || (ctx.Entities?.Entries.TryGetValue(id, out var entry) == true
                    && additionalSkip?.Invoke(entry) == true));
    }

    /// <summary>
    /// The pack to engage. Policy 0 = nearest hostile; policy 1 = rarity-weighted density
    /// (best pack), with an optional strategy corpse-weight from the active ritual block.
    /// </summary>
    public EntityCache.Entry? SelectProximityTarget(
        BehaviorContext ctx,
        bool preferDensity = false,
        Func<EntityCache.Entry, bool>? additionalSkip = null)
    {
        bool Skip(EntityCache.Entry entity)
            => IsBlacklisted(entity.Id) || additionalSkip?.Invoke(entity) == true;

        if (!preferDensity && ctx.Settings.ProximityDestinationPolicy == 0)
            return Threat.Nearest(ctx, ctx.Settings.ProximityEngageRadiusGrid, entity =>
                IsBlacklisted(entity) || additionalSkip?.Invoke(
                    ctx.Entities!.Entries[entity]) == true);
        var ritual = ctx.Strategy?.Block<Strategies.RitualBlock>();
        return Threat.BestPack(
            ctx,
            ctx.Settings.ProximityEngageRadiusGrid,
            ctx.Settings.ProximityDensityRadiusGrid,
            Skip,
            entity => ritual is { Enabled: true, DensityWeight: > 0 }
                && !string.IsNullOrEmpty(ritual.CorpseMonsterPathFragment)
                && entity.Path.Contains(ritual.CorpseMonsterPathFragment, StringComparison.OrdinalIgnoreCase)
                    ? ritual.DensityWeight
                    : 0d)?.Target;
    }

    // ── Penance Mark (curse) ────────────────────────────────────────────────

    /// <summary>
    /// Cast the profile's Mark-role skill (Penance Mark) at the highest-priority rare+ target
    /// on cooldown. Penance Mark spawns phantasms near the cursed enemy — extra attackers that
    /// feed the build's triggered damage and swarm a boss so it dies. If no rare+ is in range,
    /// still curse the biggest/nearest eligible target to grow density. Untargetable/dormant
    /// bosses can't be cursed directly — their adds get the mark instead (routing brings us
    /// close so adds spawn; see <see cref="SelectReactivationTarget"/>).
    /// </summary>
    public void MarkTick(BehaviorContext ctx)
    {
        var mark = ctx.Settings.Skills.PrimaryMark;
        // The slot's MinCastIntervalMs is the max cast frequency (tunable per skill in the web
        // UI / config). SkillBook.IsReady enforces it, so the mark is never spammed.
        if (mark is null || mark.Vk == 0 || !Skills.IsReady(mark)) return;
        var range = mark.MaxRangeGrid > 0 ? mark.MaxRangeGrid : 60f;
        var target = Threat.Biggest(ctx, range, requireLos: false, skip: IsBlacklisted);
        // Penance Mark is for RARE+ enemies only — it spawns phantasms to build pack density and
        // burst bosses. Never curse white trash (that just spams the key and snaps the cursor).
        // Threat.Biggest prefers rarity, so if the best in-range target isn't rare+, skip entirely.
        if (target is null || Threat.RarityRank(target) < 2) return;
        if (Combat.Cast(mark, Aim.AtEntity(target.Id), ctx, "penance mark") == BehaviorStatus.Success)
            Skills.MarkCast(mark);
    }

    /// <summary>
    /// The top engage destination for a proximity build: the nearest rare/unique monster
    /// (rarity-first, then distance) within range, WHETHER OR NOT it is currently targetable.
    /// Route to the boss/rare and let trash swarm it — Penance Mark + Cast-on-X + RF do the work.
    /// Untargetable/dormant bosses (a Simulacrum boss mid-phase, e.g. Kosis behind a shield) are
    /// deliberately included so the bot walks in and is already in range to mark it the instant it
    /// becomes targetable, instead of engaging surrounding trash and ignoring the boss. Skips a
    /// rare we've blacklisted for taking no damage. Returns null when no rare+ is in range — the
    /// caller then falls back to the densest pack / nearest.
    /// </summary>
    public EntityCache.Entry? SelectPriorityRare(BehaviorContext ctx, float rangeGrid)
    {
        if (ctx.Entities is null || ctx.Live is null) return null;
        var p = ctx.Live.Value.GridPosition;
        var r2 = rangeGrid * rangeGrid;
        EntityCache.Entry? best = null;
        var bestRank = -1;
        var bestD2 = float.PositiveInfinity;
        foreach (var e in ctx.Entities.Entries.Values)
        {
            if (e.IsStale) continue;
            if (e.Kind != EntityListReader.EntityKind.Monster) continue;
            if (e.Disposition != EntityDisposition.Combatant) continue;
            if (e.AlliedReaction.Truth == ObservationTruth.True) continue;
            if (EnemyIgnoreList.IsIgnored(e.Name)) continue;
            var rank = Threat.RarityRank(e);
            if (rank < 2) continue;                          // rare+ only
            if (IsBlacklisted(e.Id)) continue;               // gave up (took no damage) — let it fall back
            // Skip confirmed-dead reads; unknown life is fine (a shielded boss reads oddly).
            if (e.LifeReadable.Truth == ObservationTruth.True && (e.HpCurrent <= 0 || e.HpMax <= 0)) continue;
            float dx = e.GridPosition.X - p.X, dy = e.GridPosition.Y - p.Y;
            var d2 = dx * dx + dy * dy;
            if (d2 > r2) continue;
            if (rank > bestRank || (rank == bestRank && d2 < bestD2))
            {
                bestRank = rank; bestD2 = d2; best = e;
            }
        }
        return best;
    }

    // ── Damage-awareness ────────────────────────────────────────────────────

    /// <summary>Biggest threat we haven't given up on (skips the can't-hit blacklist).</summary>
    public EntityCache.Entry? Engageable(BehaviorContext ctx, float range, bool requireLos)
        => Threat.Biggest(ctx, range, requireLos, skip: IsBlacklisted);

    public bool IsBlacklisted(uint id)
        => _damageEvidence.IsBlacklisted(id, BotMonotonicClock.Now);

    private void UpdateEngagement(BehaviorContext ctx)
    {
        var now = BotMonotonicClock.Now;
        _damageEvidence.Tick(now);
        if (_proximityEvidenceThisTick)
            return;
        if (!_attackIssuedThisTick || ctx.Entities is null
            || !ctx.Entities.Entries.TryGetValue(_attackIssuedTargetId, out var target)
            || !TargetEligibility.IsEligible(target))
        {
            _damageEvidence.ClearEngagement();
            return;
        }

        var evidence = _damageEvidence.ObserveAcceptedAttack(
            target.Id, target.HpCurrent, now, _attackEvidenceDelayMs, BlacklistHoldMs);
        if (evidence.Outcome == DamageEvidenceOutcome.Blacklisted)
        {
            Diagnostics.EventLog.Emit(
                "combat", "combat.target-blacklisted", Diagnostics.EventSeverity.Warning,
                $"target {target.Id} took no damage within the evidence window",
                new Dictionary<string, object?>
                {
                    ["targetId"] = target.Id,
                    ["name"] = target.Name,
                    ["metadata"] = target.Metadata,
                    ["evidenceWindowMs"] = evidence.EvidenceWindowMs,
                    ["elapsedMs"] = evidence.ElapsedMs,
                    ["holdMs"] = BlacklistHoldMs,
                });
        }
    }

    private void MarkAttackIssued(uint targetId, SkillSlot slot)
    {
        _attackIssuedThisTick = true;
        _attackIssuedTargetId = targetId;
        _attackEvidenceDelayMs = Math.Max(1200.0, slot.CastTimeMs + 750.0);
    }

    public Vector2i? RetreatPoint(BehaviorContext ctx)
        => RetreatPoint(ctx, allowGapCrossing: false, RetreatStepGrid);

    private Vector2i? RetreatPoint(BehaviorContext ctx, bool allowGapCrossing, float distance)
    {
        if (ctx.Live is null || ctx.Entities is null) return null;
        var player = ctx.Live.Value.GridPosition;
        var threats = ctx.Entities.Entries.Values
            .Where(entity => TargetEligibility.IsEligible(entity))
            .Select(entity => entity.GridPosition)
            .ToArray();
        if (threats.Length == 0) return null;

        var layer = allowGapCrossing
            ? ctx.Snapshot.Nav.TargetingReader
            : ctx.Snapshot.Nav.PathReader;
        bool Valid(Vector2i candidate)
        {
            if (layer is null) return true;
            return layer.Read(candidate.X, candidate.Y) > 0
                && BubblesBot.Core.Pathfinding.PathSmoother.HasLineOfSight(
                    layer, player.X, player.Y, candidate.X, candidate.Y, minValue: 1);
        }

        return RetreatDestinationScoring.Choose(player, threats, distance, Valid);
    }

    // ── Predicates / helpers ────────────────────────────────────────────────

    public static SkillSlot? PickAttackSlot(BotSettings settings)
    {
        foreach (var s in settings.Skills.Slots)
            if (s.Role == SkillRole.Attack && s.Vk != 0) return s;
        return null;
    }

    public static bool IsFlickerStrike(SkillSlot? slot)
        => slot is { HoldToRepeat: true }
        && slot.Name.Contains("Flicker Strike", StringComparison.OrdinalIgnoreCase);

    public static float AttackRange(BehaviorContext ctx)
        => PickAttackSlot(ctx.Settings)?.MaxRangeGrid ?? DefaultAttackRange;

    public static bool LowHp(BehaviorContext ctx)
    {
        var live = ctx.Live;
        if (live is null || live.Value.HpMax <= 0) return false;
        var th = ctx.Settings.HpRetreatThreshold;
        return th > 0 && (float)live.Value.HpCurrent / live.Value.HpMax < th;
    }

    public bool ShouldUnload(BehaviorContext ctx)
        => Engageable(ctx, AttackRange(ctx), requireLos: true) is { } t && Threat.IsToughTarget(t);

    public string Posture(BehaviorContext ctx)
        => LowHp(ctx) ? "retreat" : ShouldUnload(ctx) ? "unload" : "push";

    // ── Righteous Fire (RequiredMapBuff) re-light ─────────────────────────────

    public bool RequiredMapBuffMissing(BehaviorContext ctx)
    {
        // PoE suppresses skill hotkeys while the full Ritual Favours UI is open. Let the
        // verified shop controller finish and close it before attempting the map buff.
        if (ctx.Snapshot.RitualWindow.IsVisible) return false;
        var buff = ctx.Settings.RequiredMapBuffName.Trim();
        if (buff.Length == 0) return false;
        if (ctx.Snapshot.Player is { } player && player.Buffs.Has(buff)) return false;

        // RF-style buffs burn out at low life, and re-igniting on a drained pool is a death
        // spiral. The re-light floor sits well ABOVE the douse threshold.
        if (ctx.Live is { } live && live.HpMax > 0
            && 100f * live.HpCurrent / live.HpMax < ctx.Settings.RequiredMapBuffRelightHpPercent)
            return false;
        return Threat.Biggest(ctx, RequiredBuffCombatRangeGrid, requireLos: false,
            skip: IsBlacklisted) is not null;
    }

    public BehaviorStatus EnsureRequiredMapBuffTick(BehaviorContext ctx)
    {
        var buff = ctx.Settings.RequiredMapBuffName.Trim();
        var vk = ctx.Settings.RequiredMapBuffKey;
        if (buff.Length == 0) return BehaviorStatus.Failure;
        if (vk == 0)
        {
            Diagnostics.EventLog.Emit(
                "combat", "combat.required-buff-misconfigured", Diagnostics.EventSeverity.Error,
                $"required map buff '{buff}' has no configured key");
            _rfFatalReason = $"required map buff '{buff}' has no configured key";
            return BehaviorStatus.Running;
        }

        Movement.Halt(new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live));
        var now = BotMonotonicClock.Now;
        if (_requiredBuffPulse is { IsActive: true })
            return BehaviorStatus.Running;
        _requiredBuffPulse = null;
        if ((now - _requiredBuffLastCastAt).TotalMilliseconds < 900)
            return BehaviorStatus.Running;
        if (_requiredBuffAttempts >= 5)
        {
            Diagnostics.EventLog.Emit(
                "combat", "combat.required-buff-failed", Diagnostics.EventSeverity.Error,
                $"'{buff}' was still absent after {_requiredBuffAttempts} activation attempts",
                new Dictionary<string, object?>
                {
                    ["buff"] = buff,
                    ["vk"] = vk,
                    ["attempts"] = _requiredBuffAttempts,
                });
            _rfFatalReason = $"'{buff}' still absent after {_requiredBuffAttempts} activation attempts";
            return BehaviorStatus.Running;
        }

        // Toggle skills such as RF are not reliable with a zero-duration SendInput down/up pair.
        // Hold an 80 ms pulse across ticks, then release and begin the verified retry window.
        var handle = ctx.Input.BeginHoldKey(vk,
            new HoldBudget(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(250)));
        if (handle.IsActive)
        {
            _requiredBuffPulse = handle;
            _requiredBuffPulseStartedAt = now;
        }
        return BehaviorStatus.Running;
    }

    private void AdvanceRequiredBuffPulse(BehaviorContext ctx)
    {
        if (_requiredBuffPulse is not { } pulse) return;
        if (!pulse.IsActive) { _requiredBuffPulse = null; return; }

        var now = BotMonotonicClock.Now;
        if ((now - _requiredBuffPulseStartedAt).TotalMilliseconds < RequiredBuffPulseMs)
        {
            pulse.Refresh();
            return;
        }

        pulse.Release();
        _requiredBuffPulse = null;
        _requiredBuffAttempts++;
        _requiredBuffLastCastAt = now;
        var buff = ctx.Settings.RequiredMapBuffName.Trim();
        Diagnostics.EventLog.Emit(
            "combat", "combat.required-buff-requested", Diagnostics.EventSeverity.Info,
            $"requested '{buff}' activation with {RequiredBuffPulseMs}ms pulse ({_requiredBuffAttempts}/5)",
            new Dictionary<string, object?>
            {
                ["buff"] = buff,
                ["vk"] = ctx.Settings.RequiredMapBuffKey,
                ["attempt"] = _requiredBuffAttempts,
                ["pulseMs"] = RequiredBuffPulseMs,
            });
    }

    // ── Emergency buff douse ────────────────────────────────────────────────

    public bool ShouldDouseRequiredBuff(BehaviorContext ctx)
    {
        var buff = ctx.Settings.RequiredMapBuffName.Trim();
        if (buff.Length == 0 || ctx.Settings.RequiredMapBuffKey == 0) return false;
        if (ctx.Live is not { } live || live.HpMax <= 0) return false;
        if (100f * live.HpCurrent / live.HpMax >= ctx.Settings.RequiredMapBuffMinHpPercent)
        {
            _buffLowHpSince = null;
            return false;
        }
        // Sustained-only: transient dips are the flask system's job.
        _buffLowHpSince ??= BotMonotonicClock.Now;
        if (ctx.Snapshot.Player is not { } player || !player.Buffs.Has(buff)) return false;
        if ((BotMonotonicClock.Now - _buffLowHpSince.Value).TotalSeconds < DouseAfterLowHpSeconds)
            return false;
        return _douseAttempts < MaxDouseAttempts || _dousePulse is { IsActive: true };
    }

    public BehaviorStatus DouseRequiredBuffTick(BehaviorContext ctx)
    {
        var now = BotMonotonicClock.Now;
        if (_dousePulse is { IsActive: true }) return BehaviorStatus.Running;
        _dousePulse = null;
        // ElapsedSince, never raw subtraction from the MinValue sentinel.
        if (BotMonotonicClock.ElapsedSince(_douseLastAt).TotalMilliseconds < 900)
            return BehaviorStatus.Running;

        var handle = ctx.Input.BeginHoldKey(ctx.Settings.RequiredMapBuffKey,
            new HoldBudget(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(250)));
        if (handle.IsActive)
        {
            _dousePulse = handle;
            _dousePulseStartedAt = now;
            _douseLastAt = now;
            _douseAttempts++;
            Diagnostics.EventLog.Emit(
                "combat", "combat.required-buff-douse-requested", Diagnostics.EventSeverity.Warning,
                $"HP below {ctx.Settings.RequiredMapBuffMinHpPercent}% for {DouseAfterLowHpSeconds}s+ with " +
                $"'{ctx.Settings.RequiredMapBuffName.Trim()}' burning — toggling it off ({_douseAttempts}/{MaxDouseAttempts})");
        }
        return BehaviorStatus.Running;
    }

    private void AdvanceDousePulse()
    {
        if (_dousePulse is not { } pulse) return;
        if (!pulse.IsActive) { _dousePulse = null; return; }
        if ((BotMonotonicClock.Now - _dousePulseStartedAt).TotalMilliseconds < RequiredBuffPulseMs)
        {
            pulse.Refresh();
            return;
        }
        pulse.Release();
        _dousePulse = null;
    }

    private static float Distance(Vector2i a, Vector2i b)
    {
        var dx = (float)(a.X - b.X);
        var dy = (float)(a.Y - b.Y);
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
