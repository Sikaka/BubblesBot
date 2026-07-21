using BubblesBot.Bot.Behaviors;
using BubblesBot.Bot.Behaviors.Combat;
using BubblesBot.Bot.Behaviors.Interact;
using BubblesBot.Bot.Behaviors.Loot;
using BubblesBot.Bot.Behaviors.Movement;
using BubblesBot.Bot.Input;
using BubblesBot.Bot.Settings;
using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Modes;

/// <summary>
/// Aggressive ranged "push through the map" combat for kiting bow/wand builds. Doctrine (build
/// owner): keep forward progress, ATTACK constantly as you move, only STOP to unload on
/// rares/uniques, and only actively peel away when actually in danger (low HP). We assume we're
/// strong enough that we don't kite backwards for every trash mob — we shoot through them.
///
/// <para>Three postures, top-down each tick:
/// <list type="number">
///   <item><b>Retreat</b> — low HP: kite away from the nearest enemy while still shooting the
///     biggest threat (a bow retreats firing). Flasks tick in parallel.</item>
///   <item><b>Unload</b> — a Rare/Unique is in attack range with line of sight: halt and HOLD the
///     attack on it for max DPS. LOS is required here so we never stand firing into a wall. Trash
///     (white/magic) never triggers this.</item>
///   <item><b>Push</b> (default, dominant) — explore the frontier forward and TAP the biggest
///     in-range threat every ready tick. This clears trash on the move and is where the bot
///     spends most of its time.</item>
/// </list>
/// </para>
///
/// <para><b>Design history:</b> v1 had a fourth "danger-close → blink-reposition" posture that
/// triggered on any enemy within a small radius. Against melee packs that's always true, so the
/// bot blinked nonstop and barely attacked — and died. Removed: repositioning is now only the
/// low-HP retreat, and "circling" emerges from continuing to push forward through/past packs while
/// firing at the biggest threat.</para>
///
/// <para><b>LOS split:</b> the ATTACK aims without a LOS requirement (a stray arrow into a wall is
/// harmless because movement is explore-driven, not enemy-driven — we never stall). The UNLOAD
/// trigger requires LOS, because stopping for a rare we can't actually hit WOULD stall.</para>
/// </summary>
public sealed class PushCombatMode : IBotMode
{
    private readonly Func<GameSnapshot?> _getSnapshot;
    private readonly Func<LivePlayer?>   _getLive;
    private readonly Func<EntityCache?>  _getEntities;
    private readonly Func<Strategies.FarmingStrategy?> _getStrategy;
    private readonly SettingsStore _settings;

    private readonly CombatCoordinator _coord;
    private readonly MovementSystem    _movement;
    private readonly SkillBook         _skills;
    private readonly ExplorationSystem _exploration = new();
    private readonly ExploreFrontier   _explore;
    private readonly InteractSystem    _interact = new();
    private readonly LootClosestVisible _loot;
    private readonly EnterAreaTransition _transition;
    private readonly EnterAreaTransition _completedBossArenaExit;
    private readonly FollowPath          _bossArenaSearch;
    private readonly FollowPath          _bossArenaInward;
    private readonly FollowPath          _visibleBossApproach;
    private readonly LootMemory        _lootMemory = new();
    private readonly FollowPath        _lootReturn;
    private readonly FollowPath        _lootApproach;
    private readonly InteractWorldEntity _takeShrine;
    private readonly InteractWorldEntity _takePriorityShrine;
    private readonly InteractWorldEntity _takeMemoryTear;
    private readonly TakeEldritchAltar _takeAltar;
    private readonly InteractWorldEntity _startRitual;
    private readonly RitualShopController _ritualShop;
    private readonly UltimatumMode         _ultimatum;
    private readonly DeliriumController    _delirium;
    private readonly FollowPath          _ritualEngage;
    private readonly FollowPath          _ritualRefresh;
    private readonly IBehavior         _root;
    private readonly bool _orchestrated;

    // ── Zone-loop bookkeeping — survives the per-area Reset ─────────────────
    // The loop: reveal the zone + kill packs; when exploration is exhausted (BFS dry, no
    // hostile beacon) take the nearest UNTAKEN area transition; repeat until no untaken
    // transitions remain → map complete → disarm. BotApp calls Reset() on every area
    // change, so cross-zone state lives here and is maintained in Tick, not Reset.
    private uint _currentArea;
    private Vector2i _arrivalGrid;                    // where we spawned into this zone
    private Vector2i? _transitGoal;                   // transition entity we're heading for
    private readonly List<(uint Area, Vector2i Pos)> _usedTransitions = new();
    private TimeSpan? _exhaustedSince;                // debounce for the zone-finished signal
    private bool _mapCompleteAnnounced;
    private TimeSpan _areaStartedAt = BotMonotonicClock.Now;
    private TimeSpan _lastTickAt;
    private uint _activeRitualId;
    private TimeSpan _ritualStartedAt;
    private TimeSpan? _ritualNoTargetSince;
    private Vector2i? _ritualMoveGoal;
    private Vector2i? _ritualLootAnchor;
    private TimeSpan _ritualLootMinimumUntil = TimeSpan.MinValue;
    private TimeSpan? _ritualLootQuietSince;
    private Vector2i? _ritualRefreshGoal;
    private uint _ritualRefreshTargetId;
    private float _ritualRefreshBestDistance = float.MaxValue;
    private TimeSpan _ritualRefreshLastProgressAt;
    private TimeSpan? _ritualRefreshArrivedAt;
    private readonly HashSet<uint> _ritualRefreshSkipped = new();
    private Vector2i? _lootReturnTarget;
    private float _lootReturnBestDistance = float.MaxValue;
    private TimeSpan _lootReturnLastProgressAt;
    private readonly Dictionary<uint, MechanicStatus> _mechanicStatuses = new();
    private readonly RitualPriorityTracker _ritualPriority = new();
    private readonly BossEvidenceTracker _bossTracker = new();
    private string _bossConfiguredMap = "";
    private readonly HashSet<uint> _deliriumDriveBySkipped = new();
    private TimeSpan _deliriumPackEnteredAt = TimeSpan.MinValue;
    private Vector2i? _deliriumPackAnchor;
    private bool _bossArenaEntered;
    private string? _requestedAbandonReason;
    private Vector2i? _bossArenaSearchGoal;
    private Vector2i? _bossArenaInwardGoal;
    private Vector2i _bossArenaEntryGrid;
    private int _bossArenaInwardAttempt;
    private Vector2i? _visibleBossGoal;
    private const int RitualTimeoutSeconds = 180;
    private const float RitualLeashRadius = 45f;
    private const float RitualStragglerRadius = 150f;
    private const float RitualRefreshArrivalRadius = 30f;
    private const int RitualRefreshNoProgressSeconds = 10;
    private const int RitualRefreshSettleSeconds = 3;
    private const int RitualLootMinimumSettleSeconds = 3;
    private const double RitualLootQuietSeconds = 1.25;
    // Post-ritual loot is swept, not just clicked-in-place: drive the walking sweep over the
    // circle (radius = leash + margin) so scattered drops are grabbed before the next altar
    // instead of deferred to an end-of-map backtrack.
    private const float RitualLootDrainRadius = 60f;
    private const int LootReturnNoProgressSeconds = 15;
    private const double ProximityDamageEvidenceMs = 4000;

    public string Name => "Map farming";
    public IBehavior Root => _root;
    public string LastDecision { get; private set; } = "init";

    /// <summary>
    /// Per-tick diagnostic snapshot for the dashboard's status feed: posture, current
    /// engagement + can't-hit blacklist (the ghost-mob evidence trail), entity census, and
    /// frontier progress. Built each tick on the world thread, swapped in as one reference.
    /// </summary>
    public object? Telemetry { get; private set; }

    /// <summary>Short status lines for the on-screen overlay HUD. Rebuilt each tick.</summary>
    public IReadOnlyList<string> HudLines { get; private set; } = Array.Empty<string>();
    public bool IsCleared { get; private set; }
    public string? RequestedAbandonReason => _requestedAbandonReason;

    public PushCombatMode(SettingsStore settings, CombatCoordinator coord,
        Func<GameSnapshot?> getSnapshot, Func<LivePlayer?> getLive,
        Func<EntityCache?> getEntities, bool orchestrated = false,
        Func<Strategies.FarmingStrategy?>? getStrategy = null)
    {
        _settings    = settings;
        _getSnapshot = getSnapshot;
        _getLive     = getLive;
        _getEntities = getEntities;
        _getStrategy = getStrategy ?? (() => null);
        _orchestrated = orchestrated;
        // Shared combat brain: one movement/skills authority across general-combat modes.
        _coord       = coord;
        _movement    = coord.Movement;
        _skills      = coord.Skills;
        _explore     = new ExploreFrontier("push-explore", _exploration, _movement, _skills);
        _loot        = new LootClosestVisible("loot closest", _interact, getSnapshot);
        _transition  = new EnterAreaTransition("next zone", _interact, _movement, _skills,
            getSnapshot, TransitionEligible);
        _completedBossArenaExit = new EnterAreaTransition(
            "completed boss arena exit", _interact, _movement, _skills,
            getSnapshot, CompletedBossArenaExitTransitionEligible);
        _bossArenaSearch = new FollowPath("seek boss arena", _movement,
            _ => _bossArenaSearchGoal, _skills, goalArrivalRadius: 18f);
        _bossArenaInward = new FollowPath("stage inside boss arena", _movement,
            _ => _bossArenaInwardGoal, _skills, goalArrivalRadius: 16f,
            allowGapCrossing: false);
        _visibleBossApproach = new FollowPath("approach required boss", _movement,
            _ => _visibleBossGoal, _skills,
            goalArrivalRadiusProvider: ctx => Math.Max(24f, ctx.Settings.ProximityHoldRadiusGrid * 0.75f));
        // Pack-beacons share the blacklist too — otherwise the end-of-zone mop-up would
        // walk back to an essence pack the engage branch just gave up on.
        _exploration.BeaconSkip = IsBlacklisted;
        // Accepted-loot detours: walk to remembered drops as soon as combat allows.
        // Once labels render, the ordinary loot branch (higher priority) clicks them.
        _lootReturn = new FollowPath("loot-return", _movement,
            ctx => _lootMemory.Nearest(ctx), _skills, goalArrivalRadius: 10f);
        // Walk leg of the loot branch: the sweep gate accepts labels out to LootRangeGrid,
        // but LootClosestVisible only CLICKS inside ClickRangeGrid — this closes the gap.
        _lootApproach = new FollowPath("loot approach", _movement,
            SweepLootGoal, _skills,
            goalArrivalRadius: LootClosestVisible.ClickRangeGrid * 0.6f);
        _takeShrine = new InteractWorldEntity("take shrine", _interact, _movement, _skills,
            ctx => ClosestMechanic(ctx, MechanicKind.Shrine, MechanicStatus.Available)?.Entry,
            (ctx, entry) => MechanicStatusOf(ctx, entry) == MechanicStatus.Completed,
            retryUntilActivated: true);
        // This must be a separate stateful behavior. Sharing _takeShrine across the high-priority
        // and sweep branches made Selector.ResetAfter reset its approach/click state every tick.
        _takePriorityShrine = new InteractWorldEntity("take priority shrine", _interact, _movement, _skills,
            ctx => ClosestMechanic(ctx, MechanicKind.Shrine, MechanicStatus.Available)?.Entry,
            (ctx, entry) => MechanicStatusOf(ctx, entry) == MechanicStatus.Completed,
            retryUntilActivated: true);
        // Memory tear: click → entity vanishes (status leaves Available) → an item drops a
        // few seconds later and the ordinary loot sweep collects it.
        _takeMemoryTear = new InteractWorldEntity("memory tear", _interact, _movement, _skills,
            ctx => NextMemoryTear(ctx)?.Entry,
            (ctx, entry) => MechanicStatusOf(ctx, entry) != MechanicStatus.Available);
        _takeAltar = new TakeEldritchAltar("take altar", _movement, _skills, getSnapshot,
            ctx => ctx.Entities is null
                ? Enumerable.Empty<MechanicEntry>()
                : new MechanicsView(ctx.Entities).Entries
                    .Where(m => m.Kind == MechanicKind.EldritchAltar && m.IsAvailable));
        _startRitual = new InteractWorldEntity("start ritual", _interact, _movement, _skills,
            ctx => NextAvailableRitual(ctx)?.Entry,
            (ctx, entry) => MechanicStatusOf(ctx, entry) is MechanicStatus.Active or MechanicStatus.Completed);
        _ritualShop = new RitualShopController(getSnapshot);
        _ultimatum = new UltimatumMode(settings, getSnapshot, getLive, getEntities,
            exitWhenDone: false, getStrategy: _getStrategy);
        _delirium = new DeliriumController(_movement, _skills, getSnapshot);
        _ritualEngage = new FollowPath("ritual engage", _movement,
            ctx => _ritualMoveGoal, _skills,
            goalArrivalRadiusProvider: ctx => ctx.Settings.ProximityHoldRadiusGrid);
        _ritualRefresh = new FollowPath("ritual refresh", _movement,
            ctx => _ritualRefreshGoal, _skills,
            goalArrivalRadius: RitualRefreshArrivalRadius);

        // Loot sits below UNLOAD (finish killing the rare before stopping for its drops)
        // and above PUSH — the one non-emergency case where forward motion pauses. Loot
        // clicks need a stable cursor on the label, hence the explicit halt first.
        // ENGAGE PACK (proximity stance only): deviate into nearby packs and hold among
        // them while minions/auras kill — the combat model for summoner/RF builds that
        // bring no Attack-role skill. Drive-by stance skips this branch entirely.
        // NEXT-ZONE fires only when this zone's exploration is exhausted (debounced) —
        // combat/loot branches above it still fire while walking to the door.
        _root = new Selector("push combat",
            // EMERGENCY DOUSE: an RF-style buff burning against a losing recovery race is a
            // death spiral the retreat branch cannot escape (live stalemate 2026-07-15: HP
            // oscillated 1–29% for minutes). The buff key is a toggle — turn it OFF, recover,
            // and the required-buff gate re-lights it only in combat at safe HP.
            new If("douse required buff", ShouldDouseRequiredBuff,
                new Behaviors.Action("douse required buff", DouseRequiredBuffTick)),
            new If("required map buff", RequiredMapBuffMissing,
                new Behaviors.Action("enable required map buff", EnsureRequiredMapBuffTick)),
            // Ultimatum UI, arena, reward settlement, and terminal looting are exclusive.
            // Keep this ahead of combat-recovery interactions so a newly completed encounter
            // cannot be pulled away from its asynchronously materialising reward pile.
            new If("ultimatum", ctx => _ultimatum.WantsControl(ctx)
                    && _ultimatum.RequiresExclusiveControl(ctx),
                new Behaviors.Action("ultimatum encounter", UltimatumTick)),
            // A shrine may jump ahead of combat only when it is local to the target we are
            // currently fighting. Ordinary available shrines stay in the nearest-interaction
            // sweep below; merely streaming a distant shrine never makes it globally urgent.
            new If("combat-blocking shrine", HasPriorityShrine,
                _takePriorityShrine),
            new If("active ritual", HasActiveRitual,
                new Behaviors.Action("ritual encounter", RitualTick)),
            new If("low HP", LowHp, new Behaviors.Action("retreat", RetreatTick)),
            new If("ritual loot settle", _ => _ritualLootAnchor is not null,
                new Behaviors.Action("ritual loot", RitualLootTick)),
            // A manually opened/recovered Favours window is modal: finish or close it
            // before any world movement, including remembered-loot navigation.
            new If("visible ritual rewards", ctx => ctx.Snapshot.RitualWindow.IsVisible,
                new Behaviors.Action("Ritual Favours", RitualShopTick)),
            new If("unload rare", ShouldUnload, new Behaviors.Action("unload", UnloadTick)),
            // INTERACTION SWEEP: loot, shrines, and eldritch altars are all non-combat,
            // non-deferred pickups — one nearest-first arbitration (NearestInteraction)
            // decides which branch fires so the bot sweeps them in distance order instead
            // of branch-priority order (which looted everything in range, then backtracked
            // to the shrine it had walked straight past).
            new If("loot in range", ctx => NearestInteraction(ctx) == SweepKind.Loot,
                new Behaviors.Action("loot sweep", LootSweepTick)),
            new If("take shrine", ctx => NearestInteraction(ctx) == SweepKind.Shrine,
                _takeShrine),
            new If("take altar", ctx => NearestInteraction(ctx) == SweepKind.Altar,
                _takeAltar),
            new If("memory tear", ctx => NearestInteraction(ctx) == SweepKind.MemoryTear,
                new Behaviors.Action("memory tear", MemoryTearTick)),
            // Ritual chaining sits BELOW the sweep: between encounters (no ritual actively
            // fought — that branch is at the top) any visible loot/shrine/altar is grabbed
            // on the way instead of deferring the whole floor to an end-of-map backtrack.
            new If("start ritual", ShouldStartRitual,
                new Behaviors.Action("ritual encounter", RitualTick)),
            // A mirror is a walk-through trigger (not clickable). Run to its validated
            // StateMachine anchor after local combat/loot, then let ordinary clearing own the map.
            new If("cross Delirium mirror", _delirium.WantsApproach,
                new Behaviors.Action("Delirium mirror approach", _delirium.ApproachTick)),
            // Known boss-arena maps may expose their final door before the current terrain scan
            // declares every frontier exhausted. Once the door is local, enter it: boss evidence
            // remains the whole-map completion gate, so this cannot falsely finish the map.
            new If("enter required boss arena", ShouldEnterRequiredBossArena,
                new Behaviors.Action("boss arena transition", BossArenaTransitionTick)),
            // Same-hash boss arenas do not reset the main-zone exploration cache. Once a
            // positively identified required boss streams in, route to that entity directly;
            // otherwise a proximity build can stand at the entrance outside engage range while
            // the nearby exit is the only ordinary transition objective.
            new If("approach visible required boss", ShouldApproachVisibleRequiredBoss,
                new Behaviors.Action("required boss approach", VisibleBossApproachTick)),
            // Some Strand arena layouts do not stream either boss at the entrance. Move a
            // bounded distance explicitly away from the exit until a configured boss becomes
            // visible, then let the exact-identity approach branch above take control.
            new If("stage inside required boss arena", ShouldStageInsideBossArena,
                new Behaviors.Action("boss arena inward stage", BossArenaInwardTick)),
            // Boss loot has already had first refusal above. Once the entire configured
            // arena roster is dead, take the arena exit instead of exploring this small,
            // disconnected sub-area as though it were the parent map.
            new If("exit completed boss arena", ShouldExitCompletedBossArena,
                new Behaviors.Action("completed boss arena exit", CompletedBossArenaExitTick)),
            // Late in a separate-arena map, the tile landmark is a better objective than
            // leftover coverage. Walking to it streams the actual transition entity, after
            // which the branch above owns navigation and the verified click.
            new If("seek required boss arena", ShouldSeekRequiredBossArena,
                new Behaviors.Action("boss arena search", BossArenaSearchTick)),
            // Map completion first ends the encounter, then holds a delayed-reward barrier.
            // Visible rewards are still collected by the higher-priority loot branch above.
            new If("Delirium finalization",
                ctx => _delirium.ShouldFinalize(ctx, ObjectivesComplete(ctx)),
                new Behaviors.Action("end Delirium and settle rewards", _delirium.FinalizeTick)),
            // Only exploration/backtracking is paced. Combat, incidental mechanics, and local
            // loot above this branch can always preempt the hold.
            new If("Delirium fog-front hold", _delirium.ShouldThrottle,
                new Behaviors.Action("wait for Delirium fog front", _delirium.ThrottleTick)),
            new If("refresh ritual state", ShouldRefreshRitual,
                new Behaviors.Action("ritual refresh", RefreshRitualTick)),
            new If("drain remembered loot", ctx => _lootMemory.Count > 0
                    && !_delirium.SuppressBacktracking && !BossHuntActive(ctx),
                new Behaviors.Action("loot backtrack", DrainRememberedLootTick)),
            new If("ritual rewards", ShouldHandleRitualShop,
                new Behaviors.Action("Ritual Favours", RitualShopTick)),
            new If("engage pack",
                ShouldEngagePack,
                new Behaviors.Action("proximity hold", ProximityEngageTick)),
            // The run to a known Ultimatum tile is non-exclusive: all combat/loot branches
            // above get first refusal, then routing resumes toward the altar. Once near the
            // altar or after BEGIN, the exclusive branch near the top owns every tick.
            new If("ultimatum route", ctx => _ultimatum.WantsControl(ctx),
                new Behaviors.Action("ultimatum approach", UltimatumTick)),
            new If("zone finished", ZoneFinished, new Behaviors.Action("next zone", NextZoneTick)),
            new Behaviors.Action("push", PushTick));
    }

    // ── Combat delegated to the shared CombatCoordinator ────────────────────
    // Map-farming's combat call sites (Selector branches + the ritual encounter) forward to the
    // one shared brain so map farming and Simulacrum behave identically. The coordinator owns the
    // RF re-light/douse pulse state, the damage-evidence blacklist, and the proximity engage path.

    // Map-lifecycle policy stays here: a map that forces the emergency douse twice isn't worth
    // the corpse run (see Tick — the coordinator surfaces the douse-confirmed edge).
    private int _mapDouses;

    private BehaviorStatus ProximityEngageTick(BehaviorContext ctx)
        => _coord.ProximityEngageTick(ctx, _delirium.IsEncounterActive, DeliriumTargetSkipped);

    private bool ShouldEngagePack(BehaviorContext ctx)
    {
        if (ctx.Settings.MapClearStance != 1) return false;
        if (!_delirium.IsEncounterActive)
        {
            _deliriumDriveBySkipped.Clear();
            _deliriumPackEnteredAt = TimeSpan.MinValue;
            _deliriumPackAnchor = null;
            return _coord.SelectProximityTarget(ctx) is not null;
        }

        var target = _coord.SelectProximityTarget(ctx, preferDensity: true, DeliriumTargetSkipped);
        if (target is null || ctx.Live is not { } live) return false;
        var holdRadius = ctx.Settings.ProximityHoldRadiusGrid;
        if (Distance(live.GridPosition, target.GridPosition) > holdRadius)
        {
            _deliriumPackEnteredAt = TimeSpan.MinValue;
            _deliriumPackAnchor = target.GridPosition;
            return true;
        }

        if (_deliriumPackAnchor is not { } anchor
            || Distance(anchor, target.GridPosition) > ctx.Settings.ProximityDensityRadiusGrid)
        {
            _deliriumPackAnchor = target.GridPosition;
            _deliriumPackEnteredAt = BotMonotonicClock.Now;
        }
        else if (_deliriumPackEnteredAt == TimeSpan.MinValue)
        {
            _deliriumPackEnteredAt = BotMonotonicClock.Now;
        }

        var maximumDwell = ctx.Strategy?.Block<Strategies.DeliriumBlock>()?.MaximumPackDwellSeconds ?? 3.5;
        if (!DeliriumPackPolicy.DwellExpired(
                _deliriumPackEnteredAt, BotMonotonicClock.Now, maximumDwell))
            return true;

        SkipCurrentDeliriumPack(ctx, live.GridPosition);
        Diagnostics.EventLog.Emit(
            "delirium", "delirium.pack-dwell-complete", Diagnostics.EventSeverity.Info,
            $"drive-by dwell reached {maximumDwell:F1}s; resuming forward progress",
            new Dictionary<string, object?> { ["skippedHostiles"] = _deliriumDriveBySkipped.Count });
        _deliriumPackEnteredAt = TimeSpan.MinValue;
        _deliriumPackAnchor = null;
        return false;
    }

    private bool DeliriumTargetSkipped(EntityCache.Entry entry)
        => _deliriumDriveBySkipped.Contains(entry.Id);

    private void SkipCurrentDeliriumPack(BehaviorContext ctx, Vector2i player)
    {
        if (ctx.Entities is null) return;
        var radius = Math.Max(ctx.Settings.ProximityDensityRadiusGrid, ctx.Settings.ProximityHoldRadiusGrid) + 12f;
        foreach (var entry in ctx.Entities.Entries.Values)
        {
            if (entry.IsStale
                || entry.Kind != EntityListReader.EntityKind.Monster
                || entry.Disposition != BubblesBot.Core.Knowledge.EntityDisposition.Combatant)
                continue;
            if (Distance(player, entry.GridPosition) <= radius)
                _deliriumDriveBySkipped.Add(entry.Id);
        }
    }
    private bool RequiredMapBuffMissing(BehaviorContext ctx) => _coord.RequiredMapBuffMissing(ctx);
    private BehaviorStatus EnsureRequiredMapBuffTick(BehaviorContext ctx) => _coord.EnsureRequiredMapBuffTick(ctx);
    private bool ShouldDouseRequiredBuff(BehaviorContext ctx) => _coord.ShouldDouseRequiredBuff(ctx);
    private BehaviorStatus DouseRequiredBuffTick(BehaviorContext ctx) => _coord.DouseRequiredBuffTick(ctx);

    private bool HasPriorityShrine(BehaviorContext ctx)
    {
        if (ctx.Strategy?.IsEnabled<Strategies.ShrinesBlock>() != true
            || ctx.Entities is null
            || ctx.Live is not { } live)
            return false;

        var engagedId = _coord.EngagedId;
        if (engagedId == 0
            || !ctx.Entities.Entries.TryGetValue(engagedId, out var engaged))
            return false;

        var shrine = ClosestMechanic(ctx, MechanicKind.Shrine, MechanicStatus.Available);
        if (shrine is null) return false;

        return ShrinePriorityPolicy.ShouldPreempt(
            exclusiveMechanicOwnsControl: _ultimatum.RequiresExclusiveControl(ctx),
            hasEngagedTarget: true,
            shrineDistanceToPlayer: Distance(live.GridPosition, shrine.GridPosition),
            shrineDistanceToTarget: Distance(engaged!.GridPosition, shrine.GridPosition));
    }

    private BehaviorStatus UltimatumTick(BehaviorContext ctx)
    {
        _ultimatum.Tick(ctx.Snapshot, ctx.Input);
        // Match ordinary push behavior while routing to / fighting at the altar: attacks
        // and marks may fire while movement continues. Never cast through the choice UI.
        if (!ctx.Snapshot.UltimatumPanel.IsVisible)
        {
            _coord.TapBiggestThreat(ctx);
            _coord.MarkTick(ctx);
        }
        if (_ultimatum.ShouldExitMap(ctx) && !_ultimatum.HasCollectableLoot(ctx) && !IsCleared)
        {
            IsCleared = true;
            _movement.Halt(new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live));
            Diagnostics.EventLog.Emit(
                "Ultimatum", "ultimatum.map-exit", Diagnostics.EventSeverity.Info,
                $"Ultimatum terminal state reached ({_ultimatum.LastDecision}); leaving map");
        }
        return BehaviorStatus.Running;
    }

    private enum SweepKind { None, Loot, Shrine, Altar, MemoryTear }

    private GameSnapshot? _sweepSnapshot;
    private SweepKind _sweepKind;
    private Vector2i? _sweepLootGrid;
    private float _sweepLootDist;
    private nint _sweepLootLabelAddr;

    /// <summary>
    /// Nearest-first arbitration across the non-combat interaction branches. The three tree
    /// nodes each ask which kind is globally closest, so at most one fires per tick and the
    /// sweep follows distance, not branch order. Memoized per snapshot — the tree evaluates
    /// the condition several times per tick.
    /// <para>The loot scan applies the SAME acceptance rules as <see cref="LootClosestVisible"/>
    /// (item, visible, value-filter pass, not click-blacklisted) so the gate never fires on a
    /// label the clicker would refuse — that mismatch made the branch fire and instantly fail,
    /// letting ritual chaining walk straight past fresh stacked-deck piles (live 2026-07-15).</para>
    /// </summary>
    private SweepKind NearestInteraction(BehaviorContext ctx)
    {
        if (ReferenceEquals(_sweepSnapshot, ctx.Snapshot)) return _sweepKind;
        _sweepSnapshot = ctx.Snapshot;
        _sweepLootGrid = null;
        _sweepLootDist = float.MaxValue;
        _sweepLootLabelAddr = 0;

        var best = SweepKind.None;
        var bestDist = float.MaxValue;

        var lootRange = ctx.Settings.LootRangeGrid;
        var filter = LootClosestVisible.SharedValueFilter;
        foreach (var l in ctx.Snapshot.GroundLabels)
        {
            if (!l.IsItem || !l.IsLabelVisible) continue;
            if (_loot.IsBlacklistedLabel(l.LabelAddress)) continue;
            if (l.EntityGridPosition is { } spot && _loot.IsSpotAbandoned(spot)) continue;
            var d = l.DistanceToPlayer;
            if (d > lootRange || d >= bestDist) continue;
            if (filter is not null && !filter.Evaluate(l, ctx.Settings.Loot).ShouldTake) continue;
            bestDist = d;
            best = SweepKind.Loot;
            _sweepLootGrid = l.EntityGridPosition;
            _sweepLootDist = d;
            _sweepLootLabelAddr = l.LabelAddress;
        }

        if (ctx.Live is { } live)
        {
            if (ctx.Strategy?.IsEnabled<Strategies.ShrinesBlock>() == true
                && ClosestMechanic(ctx, MechanicKind.Shrine, MechanicStatus.Available) is { } shrine)
            {
                var d = Distance(live.GridPosition, shrine.GridPosition);
                if (d < bestDist) { bestDist = d; best = SweepKind.Shrine; }
            }
            if (ctx.Strategy?.IsEnabled<Strategies.EldritchAltarsBlock>() == true
                && _takeAltar.NextCandidate(ctx) is { } altar)
            {
                var d = Distance(live.GridPosition, altar.GridPosition);
                if (d < bestDist) { bestDist = d; best = SweepKind.Altar; }
            }
            if (ctx.Strategy?.IsEnabled<Strategies.MemoryTearsBlock>() == true && NextMemoryTear(ctx) is { } tear)
            {
                var d = Distance(live.GridPosition, tear.GridPosition);
                if (d < bestDist) { bestDist = d; best = SweepKind.MemoryTear; }
            }
        }

        return _sweepKind = best;
    }

    /// <summary>Grid of the sweep's chosen loot label — the approach goal when it sits beyond
    /// <see cref="LootClosestVisible.ClickRangeGrid"/>. Null unless the sweep chose Loot.</summary>
    private Vector2i? SweepLootGoal(BehaviorContext ctx)
        => NearestInteraction(ctx) == SweepKind.Loot ? _sweepLootGrid : null;

    private bool SweepLootClickable(BehaviorContext ctx)
        => NearestInteraction(ctx) == SweepKind.Loot
            && _sweepLootDist <= MathF.Min(LootClosestVisible.ClickRangeGrid, ctx.Settings.LootRangeGrid);

    private const int LootStrikeOutSeconds = 3;
    private TimeSpan _lootStrikeSince = TimeSpan.MinValue;
    private nint _lootStrikeTarget;

    /// <summary>
    /// The loot branch: click when the sweep target is in click range, walk toward it when
    /// not. MUST return Failure whenever no forward progress is possible so the selector
    /// falls through to exploration — an earlier composite returned Success from "approach
    /// arrived" while the clicker refused the label (terrain-LOS-blocked), satisfying the
    /// tree with a no-op every tick for a full zone-failsafe window (live 2026-07-15).
    /// A target the clicker keeps refusing is struck out after <see cref="LootStrikeOutSeconds"/>
    /// and blacklisted so the sweep stops selecting it; the remembered-loot drain then owns
    /// it and its own abandonment path retires it.
    /// </summary>
    private BehaviorStatus LootSweepTick(BehaviorContext ctx)
    {
        if (SweepLootClickable(ctx))
        {
            _movement.Halt(new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live));
            var status = _loot.Tick(ctx);
            if (status != BehaviorStatus.Failure)
            {
                _lootStrikeSince = TimeSpan.MinValue;
                return BehaviorStatus.Running;
            }
            return StrikeLootTarget(ctx, "clicker refused in-range label");
        }

        var approach = _lootApproach.Tick(ctx);
        if (approach == BehaviorStatus.Success)
            return StrikeLootTarget(ctx, "arrived but label still not clickable");
        if (approach == BehaviorStatus.Failure)
            return StrikeLootTarget(ctx, "no path to label");
        _lootStrikeSince = TimeSpan.MinValue;
        return BehaviorStatus.Running;
    }

    private BehaviorStatus StrikeLootTarget(BehaviorContext ctx, string reason)
    {
        if (_lootStrikeTarget != _sweepLootLabelAddr || _lootStrikeSince == TimeSpan.MinValue)
        {
            _lootStrikeTarget = _sweepLootLabelAddr;
            _lootStrikeSince = BotMonotonicClock.Now;
        }
        else if (BotMonotonicClock.ElapsedSince(_lootStrikeSince).TotalSeconds >= LootStrikeOutSeconds)
        {
            _loot.BlacklistLabel(_lootStrikeTarget);
            Diagnostics.EventLog.Emit(
                "loot", "loot.sweep-target-struck-out", Diagnostics.EventSeverity.Warning,
                $"gave up on loot label at ({_sweepLootGrid?.X},{_sweepLootGrid?.Y}): {reason}");
            _lootStrikeSince = TimeSpan.MinValue;
            _lootStrikeTarget = 0;
        }
        // Failure either way: strikes accrue across ticks while the rest of the tree
        // (exploration, rituals) keeps making progress — movement often fixes LOS anyway.
        return BehaviorStatus.Failure;
    }

    // ── Zone loop ───────────────────────────────────────────────────────────

    /// <summary>Exploration exhausted (no frontier, no beacon) for a sustained window —
    /// the debounce absorbs the single-tick flickers around goal handoffs.</summary>
    private bool ZoneFinished(BehaviorContext ctx)
    {
        if (!MapZoneCompletionPolicy.CanAdvanceToAnotherZone(TraversalDone(ctx)))
        {
            _exhaustedSince = null;
            return false;
        }
        _exhaustedSince ??= BotMonotonicClock.Now;
        return (BotMonotonicClock.Now - _exhaustedSince.Value).TotalSeconds >= 5;
    }

    private bool ObjectivesComplete(BehaviorContext ctx)
        => TraversalDone(ctx)
        && (ctx.Strategy?.Completion.RequireBossKill != true || _bossTracker.IsComplete);

    private bool TraversalDone(BehaviorContext ctx)
    {
        if (ExplorationDone(ctx)) return true;
        var mapName = ctx.Strategy?.Supply.Map.TargetMapName ?? "";
        return ctx.Strategy?.Completion.RequireBossKill == true
            && _bossTracker.IsComplete
            && BubblesBot.Core.Knowledge.MapBossCatalog.BossCompletesTraversal(mapName);
    }

    /// <summary>
    /// Exploration is "done enough": the frontier is truly exhausted OR the reveal
    /// percentage passed the configured cutoff. Map farming is mechanics-first — the clear
    /// exists to discover mechanics and kill dense packs, not to chase the last few
    /// percent of fog for a minute per map.
    /// </summary>
    private bool ExplorationDone(BehaviorContext ctx)
    {
        if (_exploration.IsExhausted) return true;
        var cutoff = ctx.Strategy?.Completion.ExplorationDonePercent ?? 100;
        if (cutoff >= 100) return false;
        var (revealed, total) = _exploration.Progress(ctx);
        return total > 0 && 100.0 * revealed / total >= cutoff;
    }

    /// <summary>
    /// A transition the zone loop is allowed to take: a real zone door (never Portal /
    /// TownPortal — we must not accidentally leave for town mid-loop), not the one we
    /// arrived through, not one we've already taken from this zone.
    /// </summary>
    private bool TransitionEligible(EntityCache.Entry e)
    {
        if (e.Kind != EntityListReader.EntityKind.AreaTransition) return false;
        // A same-hash boss arena's nearby exit must never fall through to the generic
        // zone-finished transition branch while the required roster is incomplete.
        // Only the dedicated completed-arena branch may leave this sub-area.
        if (!MapZoneCompletionPolicy.MayUseGenericTransition(
                _bossArenaEntered, _bossTracker.IsComplete)) return false;
        long adx = e.GridPosition.X - _arrivalGrid.X, ady = e.GridPosition.Y - _arrivalGrid.Y;
        if (adx * adx + ady * ady < 30 * 30) return false;
        foreach (var (area, pos) in _usedTransitions)
            if (area == _currentArea
                && Math.Abs(pos.X - e.GridPosition.X) + Math.Abs(pos.Y - e.GridPosition.Y) < 20)
                return false;
        return true;
    }

    // A completed arena normally places its exit beside the arena spawn. That is precisely
    // what the generic anti-bounce filter rejects, so completed-arena egress uses this narrow
    // filter instead. Portals remain excluded and already-used same-hash doors remain blocked.
    private bool CompletedBossArenaExitTransitionEligible(EntityCache.Entry e)
    {
        if (e.Kind != EntityListReader.EntityKind.AreaTransition) return false;
        foreach (var (area, pos) in _usedTransitions)
            if (area == _currentArea
                && Math.Abs(pos.X - e.GridPosition.X) + Math.Abs(pos.Y - e.GridPosition.Y) < 20)
                return false;
        return true;
    }

    private bool ShouldEnterRequiredBossArena(BehaviorContext ctx)
    {
        if (ctx.Strategy?.Completion.RequireBossKill != true) return false;
        var mapName = ctx.Strategy.Supply.Map.TargetMapName;
        // Nearby positive boss identity means we are already inside the arena. The distance
        // gate matters for same-hash arenas: parent-zone scans can retain a remote boss corpse,
        // which must not reconstruct the arena latch after we have exited.
        if (BubblesBot.Core.Knowledge.MapBossCatalog.HasSeparateBossArena(mapName)
            && HasFreshRequiredBossEntity(ctx, mapName))
        {
            _bossArenaEntered = true;
            return false;
        }
        if (_bossTracker.IsComplete) return false;
        if (_bossArenaEntered) return false;
        if (!BubblesBot.Core.Knowledge.MapBossCatalog.HasSeparateBossArena(mapName))
            return false;
        // Once the entry behavior owns a door, keep that exact parent-zone coordinate
        // latched through same-hash teleport confirmation. Re-resolving here after the
        // displacement would select the nearby arena exit and incorrectly mark it used.
        if (_transitGoal is not null) return true;
        var transition = FindEligibleTransition(ctx);
        if (transition is null) return false;
        // EnterAreaTransition accepts stale transition coordinates as navigation anchors and
        // requires a fresh ground label only for the click. Once the known boss door has been
        // observed, it beats every remaining coverage frontier regardless of current distance.
        _transitGoal = transition.GridPosition;
        return true;
    }

    private static bool HasFreshRequiredBossEntity(BehaviorContext ctx, string mapName)
    {
        if (ctx.Entities is null || ctx.Live is not { } live) return false;
        var fragments = BubblesBot.Core.Knowledge.MapBossCatalog.BossFragments(mapName);
        return fragments.Count > 0 && ctx.Entities.Entries.Values.Any(entry =>
            !entry.IsStale
            && entry.Kind == EntityListReader.EntityKind.Monster
            && DistanceSquared(entry.GridPosition, live.GridPosition) <= 300L * 300L
            && fragments.Any(fragment => entry.Path.Contains(
                fragment, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool HasFreshLivingRequiredBossEntity(BehaviorContext ctx, string mapName)
    {
        if (ctx.Entities is null || ctx.Live is not { } live) return false;
        var fragments = BubblesBot.Core.Knowledge.MapBossCatalog.BossFragments(mapName);
        return fragments.Count > 0 && ctx.Entities.Entries.Values.Any(entry =>
            !entry.IsStale
            && entry.Kind == EntityListReader.EntityKind.Monster
            && DistanceSquared(entry.GridPosition, live.GridPosition) <= 300L * 300L
            && fragments.Any(fragment => entry.Path.Contains(
                fragment, StringComparison.OrdinalIgnoreCase))
            && entry.LifeReadable.Truth == ObservationTruth.True
            && entry.HpMax > 0
            && entry.HpCurrent > 0);
    }

    private static long DistanceSquared(Vector2i a, Vector2i b)
    {
        long dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private bool BossHuntActive(BehaviorContext ctx)
    {
        var mapName = ctx.Strategy?.Supply.Map.TargetMapName ?? "";
        var (revealed, total) = _exploration.Progress(ctx);
        var revealPercent = total > 0 ? 100.0 * revealed / total : 0;
        return MapZoneCompletionPolicy.ShouldPrioritizeBossArena(
            ctx.Strategy?.Completion.RequireBossKill == true,
            _bossTracker.IsComplete,
            BubblesBot.Core.Knowledge.MapBossCatalog.HasSeparateBossArena(mapName),
            _bossArenaEntered,
           revealPercent);
    }

    private bool ShouldSeekRequiredBossArena(BehaviorContext ctx)
    {
        _bossArenaSearchGoal = null;
        if (!BossHuntActive(ctx) || ctx.Live is not { } live) return false;
        if (FindEligibleTransition(ctx) is not null) return false;
        _bossArenaSearchGoal = ctx.Snapshot.TileMap.FindNearestLandmark(
            LandmarkCatalog.Kind.BossArena, live.GridPosition);
        return _bossArenaSearchGoal is not null;
    }

    private BehaviorStatus BossArenaSearchTick(BehaviorContext ctx)
    {
        var status = _bossArenaSearch.Tick(ctx);
        if (status == BehaviorStatus.Failure)
        {
            Diagnostics.EventLog.Emit(
                "maploop", "maploop.boss-arena-landmark-unreachable",
                Diagnostics.EventSeverity.Warning,
                $"could not route to boss-arena landmark {_bossArenaSearchGoal}");
            _bossArenaSearchGoal = null;
        }
        return status;
    }

    private BehaviorStatus BossArenaTransitionTick(BehaviorContext ctx)
    {
        var status = _transition.Tick(ctx);
        if (status != BehaviorStatus.Success) return status;
        MarkTransitionUsed(_transitGoal);
        _transition.Reset();
        _bossArenaEntered = true;
        if (ctx.Live is { } live)
        {
            _arrivalGrid = live.GridPosition;
            _bossArenaEntryGrid = live.GridPosition;
        }
        _bossArenaInwardGoal = null;
        _bossArenaInwardAttempt = 0;
        _bossArenaInward.Reset();
        _transitGoal = null;
        _exhaustedSince = null;
        Diagnostics.EventLog.Emit(
            "maploop", "maploop.boss-arena-entered", Diagnostics.EventSeverity.Info,
            "boss arena transition confirmed");
        return BehaviorStatus.Success;
    }

    private bool ShouldExitCompletedBossArena(BehaviorContext ctx)
    {
        var mapName = ctx.Strategy?.Supply.Map.TargetMapName ?? "";
        if (!MapZoneCompletionPolicy.ShouldExitCompletedBossArena(
                ctx.Strategy?.Completion.RequireBossKill == true,
                _bossTracker.IsComplete,
                BubblesBot.Core.Knowledge.MapBossCatalog.HasSeparateBossArena(mapName),
                _bossArenaEntered))
            return false;

        var transition = FindClosestTransition(ctx, CompletedBossArenaExitTransitionEligible);
        _transitGoal = transition?.GridPosition;
        return transition is not null;
    }

    private BehaviorStatus CompletedBossArenaExitTick(BehaviorContext ctx)
    {
        var status = _completedBossArenaExit.Tick(ctx);
        if (status != BehaviorStatus.Success) return status;

        MarkTransitionUsed(_transitGoal);
        _completedBossArenaExit.Reset();
        _bossArenaEntered = false;
        _bossArenaInwardGoal = null;
        _bossArenaInwardAttempt = 0;
        _bossArenaInward.Reset();
        _arrivalGrid = ctx.Live?.GridPosition ?? _arrivalGrid;
        _transitGoal = null;
        _exhaustedSince = null;
        Diagnostics.EventLog.Emit(
            "maploop", "maploop.boss-arena-exited", Diagnostics.EventSeverity.Info,
            "completed boss arena exit confirmed");
        return BehaviorStatus.Success;
    }

    private void MarkTransitionUsed(Vector2i? goal)
    {
        if (goal is not { } taken) return;
        if (!_usedTransitions.Any(item => item.Area == _currentArea
                && Math.Abs(item.Pos.X - taken.X) + Math.Abs(item.Pos.Y - taken.Y) < 20))
            _usedTransitions.Add((_currentArea, taken));
        if (_usedTransitions.Count > 64) _usedTransitions.RemoveAt(0);
    }

    private bool ShouldApproachVisibleRequiredBoss(BehaviorContext ctx)
    {
        _visibleBossGoal = null;
        if (ctx.Strategy?.Completion.RequireBossKill != true
            || _bossTracker.IsComplete
            || ctx.Entities is null
            || ctx.Live is not { } live)
            return false;

        var fragments = BubblesBot.Core.Knowledge.MapBossCatalog.BossFragments(
            ctx.Strategy.Supply.Map.TargetMapName);
        if (fragments.Count == 0) return false;

        EntityCache.Entry? closest = null;
        var closestD2 = float.PositiveInfinity;
        foreach (var entry in ctx.Entities.Entries.Values)
        {
            if (entry.IsStale || entry.Kind != EntityListReader.EntityKind.Monster) continue;
            if (!fragments.Any(fragment => entry.Path.Contains(
                    fragment, StringComparison.OrdinalIgnoreCase))) continue;
            if (entry.LifeReadable.Truth == ObservationTruth.True
                && (entry.HpMax <= 0 || entry.HpCurrent <= 0)) continue;

            var dx = entry.GridPosition.X - live.GridPosition.X;
            var dy = entry.GridPosition.Y - live.GridPosition.Y;
            var d2 = dx * dx + dy * dy;
            if (d2 >= closestD2) continue;
            closest = entry;
            closestD2 = d2;
        }
        if (closest is null) return false;

        var arrival = Math.Max(24f, ctx.Settings.ProximityHoldRadiusGrid * 0.75f);
        if (closestD2 <= arrival * arrival) return false;
        _bossArenaEntered = true;
        _visibleBossGoal = closest.GridPosition;
        return true;
    }

    private BehaviorStatus VisibleBossApproachTick(BehaviorContext ctx)
    {
        var status = _visibleBossApproach.Tick(ctx);
        if (status == BehaviorStatus.Failure)
        {
            Diagnostics.EventLog.Emit(
                "maploop", "maploop.visible-boss-unreachable",
                Diagnostics.EventSeverity.Warning,
                $"could not route to visible required boss at {_visibleBossGoal}");
            _visibleBossGoal = null;
        }
        return status;
    }

    private bool ShouldStageInsideBossArena(BehaviorContext ctx)
    {
        if (ctx.Strategy?.Completion.RequireBossKill != true
            || !_bossArenaEntered
            || _bossTracker.IsComplete
            || _bossArenaInwardAttempt >= 8
            || ctx.Live is null)
            return false;

        var mapName = ctx.Strategy.Supply.Map.TargetMapName;
        // A visible living boss gets the approach/combat branch above. A corpse from only
        // part of a multi-boss roster does not: continue staging until the missing boss
        // streams in, so a hot restart cannot leave through the arena exit prematurely.
        if (HasFreshLivingRequiredBossEntity(ctx, mapName)) return false;
        if (_bossArenaInwardGoal is not null) return true;

        var exit = FindClosestTransition(ctx, CompletedBossArenaExitTransitionEligible);
        if (exit is null) return false;
        if (_bossArenaEntryGrid.X == 0 && _bossArenaEntryGrid.Y == 0)
            _bossArenaEntryGrid = ctx.Live.Value.GridPosition;
        _bossArenaInwardGoal = BossArenaInwardGoal(
            _bossArenaEntryGrid, exit.GridPosition, _bossArenaInwardAttempt);
        return true;
    }

    private BehaviorStatus BossArenaInwardTick(BehaviorContext ctx)
    {
        var status = _bossArenaInward.Tick(ctx);
        if (status == BehaviorStatus.Running) return status;

        if (status == BehaviorStatus.Failure)
        {
            Diagnostics.EventLog.Emit(
                "maploop", "maploop.boss-arena-stage-retry",
                Diagnostics.EventSeverity.Warning,
                $"arena inward route {_bossArenaInwardAttempt + 1}/8 failed at {_bossArenaInwardGoal}");
        }
        _bossArenaInwardAttempt++;
        _bossArenaInwardGoal = null;
        _bossArenaInward.Reset();
        if (MapZoneCompletionPolicy.ShouldAbandonExhaustedArenaSearch(
                _bossArenaInwardAttempt, 8, _bossTracker.IsComplete))
        {
            _requestedAbandonReason =
                $"boss arena search exhausted after {_bossArenaInwardAttempt} routes with " +
                $"required roster {_bossTracker.BossesDead}/{Math.Max(_bossTracker.BossesSeen, 1)} incomplete";
            Diagnostics.EventLog.Emit(
                "maploop", "maploop.boss-arena-search-exhausted",
                Diagnostics.EventSeverity.Warning, _requestedAbandonReason);
        }
        return _bossArenaInwardAttempt >= 8 ? BehaviorStatus.Failure : BehaviorStatus.Running;
    }

    internal static Vector2i BossArenaInwardGoal(Vector2i entry, Vector2i exit, int attempt)
    {
        var dx = (double)entry.X - exit.X;
        var dy = (double)entry.Y - exit.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1) { dx = 1; dy = 0; length = 1; }
        dx /= length;
        dy /= length;

        double[] angles = [0, Math.PI / 4, -Math.PI / 4, Math.PI / 2,
            -Math.PI / 2, Math.PI * 3 / 4, -Math.PI * 3 / 4, Math.PI];
        var angle = angles[Math.Clamp(attempt, 0, angles.Length - 1)];
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        var rx = dx * cos - dy * sin;
        var ry = dx * sin + dy * cos;
        var distance = 100 + Math.Min(attempt, 4) * 25;
        return new Vector2i
        {
            X = entry.X + (int)Math.Round(rx * distance),
            Y = entry.Y + (int)Math.Round(ry * distance),
        };
    }

    private EntityCache.Entry? FindEligibleTransition(BehaviorContext ctx)
        => FindClosestTransition(ctx, TransitionEligible);

    private static EntityCache.Entry? FindClosestTransition(
        BehaviorContext ctx, Func<EntityCache.Entry, bool> eligible)
    {
        if (ctx.Entities is null || ctx.Live is null) return null;
        var p = ctx.Live.Value.GridPosition;
        EntityCache.Entry? best = null; long bestD2 = long.MaxValue;
        foreach (var e in ctx.Entities.Entries.Values)
        {
            if (!eligible(e)) continue;
            long dx = e.GridPosition.X - p.X, dy = e.GridPosition.Y - p.Y;
            var d2 = dx * dx + dy * dy;
            if (d2 < bestD2) { bestD2 = d2; best = e; }
        }
        return best;
    }

    /// <summary>Navigate to the nearest accepted remembered drop.</summary>
    private const int LootDrainBudgetSeconds = 120;
    private TimeSpan _lootDrainSince = TimeSpan.MinValue;
    private TimeSpan _lootDrainLastTickAt = TimeSpan.MinValue;

    private BehaviorStatus DrainRememberedLootTick(BehaviorContext ctx)
    {
        // Walking back re-renders the label,
        // at which point the loot branch (above this one) halts and clicks; LootMemory
        // forgets the spot once it's empty, and we resume here.
        if (_lootMemory.NearestRemembered(ctx) is { } remembered && ctx.Live is { } live)
        {
            var lootTarget = remembered.Pos;
            var now = BotMonotonicClock.Now;

            // Wall-clock ceiling on the whole backtrack phase. Per-spot no-progress
            // abandonment SHOULD terminate the drain, but one live failure mode (wobbling
            // label keys re-minting entries) looped it for a full zone-failsafe window —
            // this is the guarantee that it always ends. A >30s gap since the last drain
            // tick starts a fresh window, so an early-map detour doesn't eat the budget
            // meant for the end-of-map sweep.
            if (BotMonotonicClock.ElapsedSince(_lootDrainLastTickAt).TotalSeconds > 30)
                _lootDrainSince = now;
            _lootDrainLastTickAt = now;
            if (_lootDrainSince == TimeSpan.MinValue) _lootDrainSince = now;
            if ((now - _lootDrainSince).TotalSeconds > LootDrainBudgetSeconds)
            {
                var dropped = _lootMemory.AbandonAll();
                Diagnostics.EventLog.Emit(
                    "loot", "loot.backtrack-budget-exhausted", Diagnostics.EventSeverity.Warning,
                    $"backtrack exceeded {LootDrainBudgetSeconds}s; abandoned {dropped} remembered drops");
                return BehaviorStatus.Failure;
            }
            var changed = _lootReturnTarget is not { } previous
                || previous.X != lootTarget.X || previous.Y != lootTarget.Y;
            if (changed)
            {
                _lootReturnTarget = lootTarget;
                _lootReturnBestDistance = float.MaxValue;
                _lootReturnLastProgressAt = now;
                _lootReturn.Reset();
            }

            var distance = Distance(live.GridPosition, lootTarget);
            if (distance + 2f < _lootReturnBestDistance)
            {
                _lootReturnBestDistance = distance;
                _lootReturnLastProgressAt = now;
            }
            _lootReturn.Tick(ctx);

            if ((now - _lootReturnLastProgressAt).TotalSeconds >= LootReturnNoProgressSeconds)
            {
                _lootMemory.Forget(remembered);
                Diagnostics.EventLog.Emit(
                    "loot", "loot.backtrack-abandoned", Diagnostics.EventSeverity.Warning,
                    $"abandoned remembered loot at ({lootTarget.X},{lootTarget.Y}) after " +
                    $"{LootReturnNoProgressSeconds}s without progress",
                    new Dictionary<string, object?>
                    {
                        ["gridX"] = lootTarget.X,
                        ["gridY"] = lootTarget.Y,
                        ["distance"] = distance,
                        ["valueChaos"] = remembered.ChaosValue,
                        ["reason"] = remembered.Reason,
                        ["pathDecision"] = _lootReturn.LastDecision,
                    });
                _lootReturnTarget = null;
                _lootReturnBestDistance = float.MaxValue;
                _lootReturnLastProgressAt = now;
                _lootReturn.Reset();
            }
            return BehaviorStatus.Running;
        }

        _lootReturnTarget = null;
        _lootReturnBestDistance = float.MaxValue;

        return BehaviorStatus.Failure;
    }

    private BehaviorStatus NextZoneTick(BehaviorContext ctx)
    {
        if (_lootMemory.Count > 0 && !_delirium.SuppressBacktracking)
            return DrainRememberedLootTick(ctx);

        var bossRequired = ctx.Strategy?.Completion.RequireBossKill == true;
        var bossCompletesTraversal = BubblesBot.Core.Knowledge.MapBossCatalog.BossCompletesTraversal(
            ctx.Strategy?.Supply.Map.TargetMapName ?? "");
        if (MapZoneCompletionPolicy.CanCompleteMap(
                ExplorationDone(ctx), bossRequired, _bossTracker.IsComplete, _delirium.IsSettled,
                bossCompletesTraversal))
        {
            IsCleared = true;
            if (!_mapCompleteAnnounced)
            {
                Diagnostics.EventLog.Log("maploop",
                    $"map complete: objectives satisfied in zone {_currentArea} ({_usedTransitions.Count} transitions taken) - disarming");
                if (!_orchestrated) _settings.Mutate(s => s.BotActive = false);
                _mapCompleteAnnounced = true;
            }
            _movement.Halt(new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live));
            return BehaviorStatus.Running;
        }

        var target = FindEligibleTransition(ctx);
        _transitGoal = target?.GridPosition;
        if (target is not null) return _transition.Tick(ctx);

        if (!MapZoneCompletionPolicy.CanCompleteMap(
                ExplorationDone(ctx), bossRequired, _bossTracker.IsComplete, _delirium.IsSettled,
                bossCompletesTraversal))
        {
            _movement.Halt(new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live));
            LastDecision = bossRequired && !_bossTracker.IsComplete
                ? "zone exhausted; waiting for required boss transition/evidence"
                : "zone exhausted; waiting for Delirium settlement";
            return BehaviorStatus.Running;
        }

        return BehaviorStatus.Running;
    }

    public void Reset()
    {
        _movement.Release();
        _coord.ResetCombat();
        // NOTE: _exploration is deliberately NOT reset here. Reset() fires on every area
        // change (BotApp), and the zone loop needs cross-zone reveal memory so revisited
        // zones read as exhausted. ExplorationSystem swaps per-area state itself.
        _explore.Reset();
        _interact.Cancel();
        _loot.Reset();
        _transition.Reset();
        _completedBossArenaExit.Reset();
        _bossArenaSearch.Reset();
        _bossArenaInward.Reset();
        _visibleBossApproach.Reset();
        _lootReturn.Reset();
        _lootApproach.Reset();
        _takeShrine.Reset();
        _takePriorityShrine.Reset();
        _takeMemoryTear.Reset();
        _takeAltar.Reset();
        _startRitual.Reset();
        _ritualShop.Reset();
        _ultimatum.Reset();
        _delirium.Reset();
        _ritualEngage.Reset();
        _ritualRefresh.Reset();
        _activeRitualId = 0;
        _ritualStartedAt = TimeSpan.Zero;
        _ritualNoTargetSince = null;
        _ritualMoveGoal = null;
        _ritualLootAnchor = null;
        _ritualLootMinimumUntil = TimeSpan.MinValue;
        _ritualLootQuietSince = null;
        _ritualRefreshGoal = null;
        _ritualRefreshTargetId = 0;
        _ritualRefreshBestDistance = float.MaxValue;
        _ritualRefreshLastProgressAt = TimeSpan.Zero;
        _ritualRefreshArrivedAt = null;
        _ritualRefreshSkipped.Clear();
        _lootReturnTarget = null;
        _lootReturnBestDistance = float.MaxValue;
        _lootReturnLastProgressAt = TimeSpan.Zero;
        _lootStrikeSince = TimeSpan.MinValue;
        _lootStrikeTarget = 0;
        _lootDrainSince = TimeSpan.MinValue;
        _lootDrainLastTickAt = TimeSpan.MinValue;
        _mapDouses = 0;
        _mechanicStatuses.Clear();
        _ritualsFought.Clear();
        _allRitualsCompleteSince = null;
        _memoryTearsResolved.Clear();
        _ritualPriority.Reset();
        _bossTracker.Reset();
        _bossConfiguredMap = "";
        _deliriumDriveBySkipped.Clear();
        _deliriumPackEnteredAt = TimeSpan.MinValue;
        _deliriumPackAnchor = null;
        _bossArenaEntered = false;
        _requestedAbandonReason = null;
        _bossArenaSearchGoal = null;
        _bossArenaInwardGoal = null;
        _bossArenaEntryGrid = default;
        _bossArenaInwardAttempt = 0;
        _visibleBossGoal = null;
        // _lootMemory intentionally not reset — it's per-area internally, and Reset() fires
        // on every zone hop (same rationale as _exploration).
        _exhaustedSince = null;
        _root.Reset();
        LastDecision = "reset";
    }

    public void Tick(GameSnapshot snapshot, IInputRouter input)
    {
        _coord.BeginTick(snapshot);
        var ctx = new BehaviorContext(snapshot, input, _settings.Current, _getLive(), _getEntities(), _getStrategy());

        // Zone-loop bookkeeping. Modes only tick while armed, so a gap in ticks means we
        // were paused/disarmed — restart the zone timer rather than counting idle time.
        var now0 = BotMonotonicClock.Now;
        if ((now0 - _lastTickAt).TotalSeconds > 5) _areaStartedAt = now0;
        _lastTickAt = now0;
        if (snapshot.AreaHash != 0 && snapshot.AreaHash != _currentArea)
        {
            if (_currentArea != 0 && _transitGoal is { } taken)
            {
                _usedTransitions.Add((_currentArea, taken));
                if (_usedTransitions.Count > 64) _usedTransitions.Clear();
            }
            _currentArea = snapshot.AreaHash;
            IsCleared = false;
            _mapDouses = 0;
            _arrivalGrid = ctx.Live?.GridPosition ?? default;
            _transitGoal = null;
            _exhaustedSince = null;
            _mapCompleteAnnounced = false;
            _areaStartedAt = now0;
            _mechanicStatuses.Clear();
            _ritualsFought.Clear();
            _allRitualsCompleteSince = null;
            _memoryTearsResolved.Clear();
            _activeRitualId = 0;
            _ritualStartedAt = TimeSpan.Zero;
            _ritualNoTargetSince = null;
            _ritualMoveGoal = null;
            _ritualLootAnchor = null;
            _ritualLootMinimumUntil = TimeSpan.MinValue;
            _ritualLootQuietSince = null;
            _coord.OnAreaChanged();
            Diagnostics.EventLog.Log("maploop",
                $"entered zone {_currentArea} at ({_arrivalGrid.X},{_arrivalGrid.Y}); transitions taken so far: {_usedTransitions.Count}");
        }

        // Max-time-per-zone failsafe: totally stuck → bail early by disarming. The strategy may
        // override the profile failsafe; a null override inherits the profile value.
        var maxMin = _getStrategy()?.Limits.MaxZoneMinutes ?? _settings.Current.MaxZoneMinutes;
        if (maxMin > 0 && (now0 - _areaStartedAt).TotalMinutes > maxMin)
        {
            Diagnostics.EventLog.Log("maploop",
                $"FAILSAFE: {maxMin} min in zone {_currentArea} without finishing — disarming");
            _settings.Mutate(s => s.BotActive = false);
            _areaStartedAt = now0;   // no re-fire spam if the user re-arms to investigate
            _movement.Release();
            return;
        }

        // Propagate persistent-cover give-ups (position-keyed) into LootMemory so the
        // end-of-zone backtrack never walks to a drop we already wrote off as unlootable.
        foreach (var spot in _loot.AbandonedSpots) _lootMemory.AbandonSpot(spot);
        _lootMemory.Track(ctx);  // remember valuable drops for the end-of-zone sweep
        UpdateBossEvidence(ctx);
        _delirium.Observe(ctx);
        UpdateStackedDeckObservations(ctx);
        UpdateMechanicEvents(ctx);
        _coord.PreRoot(ctx);           // flasks + RF/douse pulse advance (shared)
        _root.Tick(ctx);
        // Post-root maintenance lives in the coordinator (RF confirm, damage-evidence, held-key
        // release). Map-lifecycle policy stays here: disarm on RF misconfig, abandon on 2x douse.
        var combat = _coord.PostRoot(ctx);
        if (combat.FatalReason is { } rfFatal)
        {
            Diagnostics.EventLog.Emit("combat", "combat.required-buff-fatal",
                Diagnostics.EventSeverity.Error, $"{rfFatal}; disarming");
            _settings.Mutate(s => s.BotActive = false);
        }
        if (combat.DouseConfirmed)
        {
            _mapDouses++;
            // A map that forces the emergency douse twice (hostile recovery mods, cursed altar
            // combo) is not worth the corpse run — declare it done and let the loop move on.
            if (_mapDouses >= 2 && !IsCleared)
            {
                IsCleared = true;
                Diagnostics.EventLog.Emit(
                    "maploop", "maploop.map-abandoned", Diagnostics.EventSeverity.Warning,
                    $"abandoning map: required buff doused {_mapDouses}x — recovery cannot sustain it here");
            }
        }

        var posture = _coord.Posture(ctx);
        LastDecision = $"{posture} eng={_coord.EngagedId} skip={_coord.BlacklistCount}";

        var now2 = BotMonotonicClock.Now;
        var census = Diagnostics.TelemetrySnapshot.EntityCensus(ctx);
        var (revealed, totalQuanta) = _exploration.Progress(ctx);
        var pct = totalQuanta > 0 ? (int)Math.Round(100.0 * revealed / totalQuanta) : 0;
        var zoneFinished = TraversalDone(ctx);
        var mechanics = MechanicTelemetry(ctx);
        var zoneMin = (int)(now2 - _areaStartedAt).TotalMinutes;
        Telemetry = new
        {
            posture,
            engagedId = _coord.EngagedId,
            engagedForMs = (int)_coord.EngagedForMs(now2),
            blacklist = _coord.Blacklist(now2).Take(10)
                .Select(item => new { id = item.Id, remainMs = (int)item.RemainingMs })
                .ToArray(),
            explorePct = pct,
            exploreRevealed = revealed,
            exploreTotal = totalQuanta,
            zoneFinished,
            zoneMinutes = zoneMin,
            transitionsTaken = _usedTransitions.Count,
            lootRemembered = _lootMemory.Count,
            boss = new
            {
                configured = _bossTracker.HasExpectedBosses,
                complete = _bossTracker.IsComplete,
                seen = _bossTracker.BossesSeen,
                dead = _bossTracker.BossesDead,
                huntActive = BossHuntActive(ctx),
                arenaEntered = _bossArenaEntered,
                freshRequiredEntity = HasFreshRequiredBossEntity(
                    ctx, ctx.Strategy?.Supply.Map.TargetMapName ?? ""),
            },
            delirium = _delirium.Telemetry(ctx),
            mechanics,
            census,
            explore   = Diagnostics.TelemetrySnapshot.ExploreState(_explore.Follow, _exploration),
        };
        HudLines = new[]
        {
            $"PUSH [{(zoneFinished ? "zone done -> next" : posture)}]  reveal {pct}% ({revealed}/{totalQuanta})  zone {zoneMin}m",
            $"hostiles {census.HostileAlive} ({census.Targetable} targetable, {census.Dormant} dormant, {census.Hazards} hazards, {census.Allies} allies)",
            $"target {(census.NearestTargetable.Length > 0 ? census.NearestTargetable : "-")}  blacklist {_coord.BlacklistCount}  doors {_usedTransitions.Count}  loot-mem {_lootMemory.Count}",
            $"mechanics shrine {mechanics.shrinesAvailable}/{mechanics.shrinesSeen}  altar {mechanics.altarsAvailable}/{mechanics.altarsSeen} taken {mechanics.altarsTaken}  ritual {mechanics.ritualFresh}/{mechanics.ritualActive}/{mechanics.ritualComplete}",
            $"Delirium {_delirium.CurrentPhase}: {_delirium.LastDecision}  boss {_bossTracker.BossesDead}/{Math.Max(_bossTracker.BossesSeen, BubblesBot.Core.Knowledge.MapBossCatalog.BossFragments(ctx.Strategy?.Supply.Map.TargetMapName ?? "").Count)}",
            $"move: {(zoneFinished ? _transition.Name + ": " : "")}{_explore.Follow.LastDecision}",
        };
    }

    // ── Map mechanics ───────────────────────────────────────────────────────

    private void UpdateBossEvidence(BehaviorContext ctx)
    {
        var mapName = ctx.Strategy?.Supply.Map.TargetMapName?.Trim() ?? "";
        if (!mapName.Equals(_bossConfiguredMap, StringComparison.OrdinalIgnoreCase))
        {
            _bossConfiguredMap = mapName;
            _bossTracker.Configure(BubblesBot.Core.Knowledge.MapBossCatalog.BossFragments(mapName));
        }
        if (!_bossTracker.HasExpectedBosses || ctx.Entities is null || ctx.Live is not { } live)
            return;

        var monsters = ctx.Entities.Entries.Values
            .Where(entry => !entry.IsStale && entry.HasLife)
            .Select(entry => new BossObservation(
                entry.Id, entry.Path, entry.GridPosition, entry.HpCurrent, entry.HpMax));
        _bossTracker.Observe(monsters, live.GridPosition, ctx.Entities.LastScanHealth.Healthy);
    }

    /// <summary>The active ritual block, if the strategy enables Ritual.</summary>
    private static Strategies.RitualBlock? RitualCfg(BehaviorContext ctx)
    {
        var block = ctx.Strategy?.Block<Strategies.RitualBlock>();
        return block is { Enabled: true } ? block : null;
    }

    /// <summary>
    /// True for the corpse-ordered Ritual strategy (Cloister stacked decks): freeze the
    /// per-altar corpse census, order the chain by corpse count, revisit unknown altars, and
    /// bonus-weight corpse-monster packs. Replaces the legacy <c>MapFarmPreset == 1</c> gate.
    /// </summary>
    private static bool CorpseOrderedRitual(BehaviorContext ctx)
        => RitualCfg(ctx) is { ChainOrdering: Strategies.RitualChainOrdering.CloisterCorpses };

    private bool HasActiveRitual(BehaviorContext ctx)
        => RitualCfg(ctx) is not null
            && ClosestMechanic(ctx, MechanicKind.RitualRune, MechanicStatus.Active) is not null;

    private bool ShouldHandleRitualShop(BehaviorContext ctx)
    {
        if (RitualCfg(ctx) is not { Shop.Enabled: true } || _ritualShop.IsDone)
            return false;
        if (ctx.Snapshot.RitualWindow.IsVisible) return true;
        if (!ZoneFinished(ctx) || _lootMemory.Count > 0) return false;
        if (ctx.Entities is null) return false;
        var rituals = new MechanicsView(ctx.Entities).Entries
            .Where(x => x.Kind == MechanicKind.RitualRune)
            .ToArray();
        var allComplete = rituals.Length > 0
            && rituals.All(x => EffectiveMechanicStatus(x) == MechanicStatus.Completed);
        // Debounced: raw ritual state reads flicker, and one transient state=3 tick must
        // not start spending tribute while a ritual is still standing.
        if (!allComplete) { _allRitualsCompleteSince = null; return false; }
        _allRitualsCompleteSince ??= BotMonotonicClock.Now;
        return (BotMonotonicClock.Now - _allRitualsCompleteSince.Value).TotalSeconds >= 2;
    }

    private TimeSpan? _allRitualsCompleteSince;

    private BehaviorStatus RitualShopTick(BehaviorContext ctx)
    {
        _movement.Halt(new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live));
        if (!ctx.Snapshot.RitualWindow.IsVisible && !_ritualShop.HasPendingAction)
        {
            var button = ctx.Snapshot.RitualRewardsButton;
            if (!button.IsVisible || button.ClickRect is not { } rect)
                return BehaviorStatus.Running;
            var (sx, sy) = ctx.Snapshot.Window.ToScreen((int)rect.CenterX, (int)rect.CenterY);
            ctx.Input.Click(sx, sy, ClickIntent.InteractUi, "open global Ritual Favours",
                expectResolved: () => _getSnapshot()?.RitualWindow.IsVisible == true,
                timeoutMs: 2000);
            return BehaviorStatus.Running;
        }
        return _ritualShop.Tick(ctx);
    }

    private bool ShouldStartRitual(BehaviorContext ctx)
    {
        var ritual = RitualCfg(ctx);
        if (ritual is null) return false;
        if (ritual.DeferUntilMapSweep && !ExplorationDone(ctx) && !_delirium.IsEncounterActive)
            return false;
        if (CorpseOrderedRitual(ctx) && ritual.DeferUntilMapSweep)
        {
            var wasFrozen = _ritualPriority.IsFrozen;
            _ritualPriority.Freeze();
            if (!wasFrozen)
                Diagnostics.EventLog.Emit(
                    "ritual", "ritual.priority-order-frozen", Diagnostics.EventSeverity.Info,
                    $"froze {_ritualPriority.AltarsTracked} Ritual scores from " +
                    $"{_ritualPriority.PriorityDead}/{_ritualPriority.PrioritySeen} dead Cloister monsters",
                    new Dictionary<string, object?>
                    {
                        ["altars"] = _ritualPriority.AltarsTracked,
                        ["prioritySeen"] = _ritualPriority.PrioritySeen,
                        ["priorityDead"] = _ritualPriority.PriorityDead,
                        ["scores"] = string.Join(",", _ritualPriority.Scores
                            .OrderByDescending(pair => pair.Value)
                            .Select(pair => $"{pair.Key}:{pair.Value}")),
                    });
        }
        var next = NextAvailableRitual(ctx);
        if (next is null) return false;
        // In fog, Ritual is incidental: run it when encountered, never route back across the
        // annulus to a cached altar. Active rituals are handled by the exclusive branch above.
        return !_delirium.IsEncounterActive
            || ctx.Live is { } live && Distance(live.GridPosition, next.GridPosition) <= 80;
    }

    /// <summary>
    /// Starting a Ritual can leave cached off-screen altars at raw state 0/Unknown until they
    /// re-enter the network bubble. Every altar matters for the Cloister strategy, so revisit
    /// unresolved positions explicitly before loot backtracking or map completion.
    /// </summary>
    private bool ShouldRefreshRitual(BehaviorContext ctx)
        => CorpseOrderedRitual(ctx)
            && _ritualPriority.IsFrozen
            && !_ritualShop.IsDone
            && NextUnknownRitual(ctx) is not null;

    private MechanicEntry? NextUnknownRitual(BehaviorContext ctx)
    {
        if (ctx.Entities is null || ctx.Live is null) return null;
        var player = ctx.Live.Value.GridPosition;
        return new MechanicsView(ctx.Entities).Entries
            .Where(mechanic => mechanic.Kind == MechanicKind.RitualRune
                            && EffectiveMechanicStatus(mechanic) == MechanicStatus.Unknown
                            && !_ritualRefreshSkipped.Contains(mechanic.Id))
            .OrderByDescending(mechanic => _ritualPriority.CorpseCount(mechanic.Id))
            .ThenBy(mechanic => Distance(player, mechanic.GridPosition))
            .FirstOrDefault();
    }

    private BehaviorStatus RefreshRitualTick(BehaviorContext ctx)
    {
        var target = NextUnknownRitual(ctx);
        if (target is null || ctx.Live is null) return BehaviorStatus.Failure;
        var now = BotMonotonicClock.Now;
        var distance = Distance(ctx.Live.Value.GridPosition, target.GridPosition);

        if (_ritualRefreshTargetId != target.Id)
        {
            _ritualRefreshTargetId = target.Id;
            _ritualRefreshGoal = target.GridPosition;
            _ritualRefreshBestDistance = distance;
            _ritualRefreshLastProgressAt = now;
            _ritualRefreshArrivedAt = null;
            _ritualRefresh.Reset();
            Diagnostics.EventLog.Emit(
                "ritual", "ritual.refresh-started", Diagnostics.EventSeverity.Info,
                $"refreshing unresolved Ritual {target.Id} score=" +
                _ritualPriority.CorpseCount(target.Id),
                new Dictionary<string, object?>
                {
                    ["altarId"] = target.Id,
                    ["gridX"] = target.GridPosition.X,
                    ["gridY"] = target.GridPosition.Y,
                    ["priorityCorpseScore"] = _ritualPriority.CorpseCount(target.Id),
                });
        }

        if (distance + 2f < _ritualRefreshBestDistance)
        {
            _ritualRefreshBestDistance = distance;
            _ritualRefreshLastProgressAt = now;
        }

        if (distance > RitualRefreshArrivalRadius)
        {
            _ritualRefreshArrivedAt = null;
            _ritualRefresh.Tick(ctx);
            if ((now - _ritualRefreshLastProgressAt).TotalSeconds < RitualRefreshNoProgressSeconds)
                return BehaviorStatus.Running;
            return AbandonRitualRefresh(ctx, target, distance, "navigation made no progress");
        }

        _movement.Halt(new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live));
        _ritualRefreshArrivedAt ??= now;
        if ((now - _ritualRefreshArrivedAt.Value).TotalSeconds < RitualRefreshSettleSeconds)
            return BehaviorStatus.Running;
        return AbandonRitualRefresh(ctx, target, distance, "state remained unknown in network range");
    }

    private BehaviorStatus AbandonRitualRefresh(
        BehaviorContext ctx, MechanicEntry target, float distance, string reason)
    {
        _ritualRefreshSkipped.Add(target.Id);
        Diagnostics.EventLog.Emit(
            "ritual", "ritual.refresh-failed", Diagnostics.EventSeverity.Error,
            $"Ritual {target.Id} refresh failed: {reason}",
            new Dictionary<string, object?>
            {
                ["altarId"] = target.Id,
                ["distance"] = distance,
                ["reason"] = reason,
                ["pathDecision"] = _ritualRefresh.LastDecision,
            });
        _ritualRefreshTargetId = 0;
        _ritualRefreshGoal = null;
        _ritualRefreshArrivedAt = null;
        _ritualRefresh.Reset();
        _movement.Halt(new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live));
        return BehaviorStatus.Running;
    }

    private MechanicEntry? NextAvailableRitual(BehaviorContext ctx)
    {
        if (ctx.Entities is null || ctx.Live is null) return null;
        var available = new MechanicsView(ctx.Entities).Entries
            .Where(mechanic => mechanic.Kind == MechanicKind.RitualRune
                            && EffectiveMechanicStatus(mechanic) == MechanicStatus.Available)
            .ToArray();
        if (!CorpseOrderedRitual(ctx) || !_ritualPriority.IsFrozen)
            return ClosestMechanic(ctx, MechanicKind.RitualRune, MechanicStatus.Available);
        return StackedDeckPolicy.ChooseRitual(
            available, ctx.Live.Value.GridPosition, _ritualPriority.CorpseCount);
    }

    private void UpdateStackedDeckObservations(BehaviorContext ctx)
    {
        if (RitualCfg(ctx) is not { ChainOrdering: Strategies.RitualChainOrdering.CloisterCorpses } ritual
            || ctx.Entities is null || ctx.Live is null)
            return;
        _ritualPriority.Configure(ritual.CorpseMonsterPathFragment, ritual.CorpseRadiusGrid);
        _ritualPriority.Observe(ctx.Entities, ctx.Live.Value.GridPosition);
        _ritualPriority.RegisterAltars(new MechanicsView(ctx.Entities).Entries);
    }

    private BehaviorStatus RitualTick(BehaviorContext ctx)
    {
        var active = ClosestMechanic(ctx, MechanicKind.RitualRune, MechanicStatus.Active);
        if (active is null)
        {
            _activeRitualId = 0;
            _ritualStartedAt = TimeSpan.Zero;
            return _startRitual.Tick(ctx);
        }

        if (_activeRitualId != active.Id)
        {
            _activeRitualId = active.Id;
            _ritualStartedAt = BotMonotonicClock.Now;
            _ritualNoTargetSince = null;
            _ritualMoveGoal = null;
            _startRitual.Reset();
            Diagnostics.EventLog.Emit("ritual", "ritual.activated", Diagnostics.EventSeverity.Info,
                $"ritual altar {active.Id} entered active state",
                new Dictionary<string, object?>
                {
                    ["altarId"] = active.Id,
                    ["gridX"] = active.GridPosition.X,
                    ["gridY"] = active.GridPosition.Y,
                    ["state"] = active.Entry.RitualCurrentState.Value,
                    ["priorityCorpseScore"] = _ritualPriority.CorpseCount(active.Id),
                });
        }

        if ((BotMonotonicClock.Now - _ritualStartedAt).TotalSeconds > RitualTimeoutSeconds)
        {
            Diagnostics.EventLog.Emit("ritual", "ritual.timeout", Diagnostics.EventSeverity.Critical,
                $"ritual altar {active.Id} remained active for {RitualTimeoutSeconds}s; disarming",
                new Dictionary<string, object?> { ["altarId"] = active.Id, ["timeoutSeconds"] = RitualTimeoutSeconds });
            _settings.Mutate(s => s.BotActive = false);
            _movement.Release();
            return BehaviorStatus.Running;
        }

        // Ritual combat owns movement until state=3. Attack builds keep firing; RF/minion
        // builds path into target clusters and hold at their configured proximity radius.
        var threat = ClosestRitualThreat(ctx, RitualLeashRadius);
        if (threat is null)
        {
            _ritualNoTargetSince ??= BotMonotonicClock.Now;
            if ((BotMonotonicClock.Now - _ritualNoTargetSince.Value).TotalSeconds >= 5)
                threat = ClosestRitualThreat(ctx, RitualStragglerRadius);
        }
        else _ritualNoTargetSince = null;
        _ritualMoveGoal = threat?.GridPosition;
        if (LowHp(ctx))
        {
            RitualRetreatTick(ctx, active.GridPosition);
            return BehaviorStatus.Running;
        }
        if (ShouldUnload(ctx))
        {
            UnloadTick(ctx);
            return BehaviorStatus.Running;
        }
        if (threat is null)
        {
            _movement.Halt(new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live));
            MaybeLogRitualStall(ctx, active);
            return BehaviorStatus.Running;
        }

        if (ctx.Live is { } live)
        {
            var d = Distance(live.GridPosition, threat.GridPosition);
            if (d <= ctx.Settings.ProximityHoldRadiusGrid)
                _movement.Halt(new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live));
            else
                _ritualEngage.Tick(ctx);
        }
        // Last-resort case: every eligible monster in the circle is blacklisted, and
        // TapBiggestThreat skips blacklisted ids — aim at the chosen threat explicitly so
        // the round can still finish. Damage evidence re-blacklists it if still immune.
        if (IsBlacklisted(threat.Id)) TapThreat(ctx, threat, "ritual last-resort tap");
        else TapBiggestThreat(ctx);
        return BehaviorStatus.Running;
    }

    private TimeSpan _ritualStallLoggedAt = TimeSpan.MinValue;

    /// <summary>
    /// Ground truth for "the ritual is active but the bot targets nothing": every ~6s of
    /// stall, dump each Monster-kind entity near the anchor with its exact rejection
    /// reason and blacklist state. This is how we catch the next ignored-totem class bug
    /// instead of guessing (live report 2026-07-15: a required generic totem was ignored).
    /// </summary>
    private void MaybeLogRitualStall(BehaviorContext ctx, MechanicEntry active)
    {
        if (_ritualNoTargetSince is null || ctx.Entities is null) return;
        if ((BotMonotonicClock.Now - _ritualNoTargetSince.Value).TotalSeconds < 5) return;
        if (BotMonotonicClock.ElapsedSince(_ritualStallLoggedAt).TotalSeconds < 6) return;
        _ritualStallLoggedAt = BotMonotonicClock.Now;

        var lines = new List<string>();
        foreach (var entry in ctx.Entities.Entries.Values)
        {
            if (entry.Kind != EntityListReader.EntityKind.Monster) continue;
            long ax = entry.GridPosition.X - active.GridPosition.X;
            long ay = entry.GridPosition.Y - active.GridPosition.Y;
            if (ax * ax + ay * ay > (long)(RitualStragglerRadius * RitualStragglerRadius)) continue;
            var verdict = TargetEligibility.Evaluate(entry);
            if (verdict.Accepted && !IsBlacklisted(entry.Id)) continue; // would be targeted
            lines.Add($"id={entry.Id} hp={entry.HpCurrent}/{entry.HpMax} " +
                      $"reject={(verdict.Accepted ? "blacklist" : verdict.Reason.ToString())} " +
                      $"stale={entry.IsStale} path='{entry.Metadata}'");
            if (lines.Count >= 12) break;
        }
        Diagnostics.EventLog.Emit("ritual", "ritual.stall-census", Diagnostics.EventSeverity.Warning,
            lines.Count == 0
                ? $"ritual {active.Id} active with no threats found and no rejected monsters near anchor"
                : $"ritual {active.Id} active but all nearby monsters rejected: " + string.Join(" | ", lines));
    }

    private BehaviorStatus RitualLootTick(BehaviorContext ctx)
    {
        if (_ritualLootAnchor is null) return BehaviorStatus.Failure;
        var anchor = _ritualLootAnchor.Value;
        var now = BotMonotonicClock.Now;

        // Ritual drops scatter across the whole circle (RitualLeashRadius), far beyond the
        // clicker's ClickRangeGrid. The old settle stood at the altar and clicked only what
        // was already under it, stranding the rest for an end-of-map backtrack (live: the bot
        // chained every altar, THEN walked the map over for the loot). Drive the ordinary
        // walking sweep here instead so it strolls the circle grabbing drops; walking
        // re-renders neighbouring off-screen labels, so it drains the circle on its own.
        if (NearestInteraction(ctx) == SweepKind.Loot)
        {
            _ritualLootQuietSince = null;
            var swept = LootSweepTick(ctx);
            // Stay parked in the settle even when the sweep strikes a label out (Failure): the
            // strike-out blacklists it, so next tick it's gone and we move on. Falling through
            // to Failure here would hand control to the ritual-chaining branch below with
            // drops still on the floor — the very backtrack we're removing.
            return swept == BehaviorStatus.Failure ? BehaviorStatus.Running : swept;
        }

        // Nothing accepted is rendered in range. A drop we remembered during the fight may
        // have fallen off-screen inside the circle (straggler killed at the leash edge, drop
        // behind fog) — walk to it so its label re-renders and the sweep above grabs it. Both
        // the player and the drop must be inside the circle so a chain never wanders off to
        // another altar's or room's loot before this altar is finished.
        if (ctx.Live is { } live
            && _lootMemory.NearestRemembered(ctx) is { } near
            && Distance(live.GridPosition, anchor) <= RitualLootDrainRadius
            && Distance(near.Pos, anchor) <= RitualLootDrainRadius)
        {
            _ritualLootQuietSince = null;
            return DrainRememberedLootTick(ctx);
        }

        // Circle is drained. Hold at the altar for late drops, then release the chain once the
        // minimum settle has elapsed and the ground has stayed quiet.
        _movement.Halt(new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live));
        if (now < _ritualLootMinimumUntil)
            return BehaviorStatus.Running;
        _ritualLootQuietSince ??= now;
        if ((now - _ritualLootQuietSince.Value).TotalSeconds < RitualLootQuietSeconds)
            return BehaviorStatus.Running;

        Diagnostics.EventLog.Emit(
            "ritual", "ritual.loot-settle-completed", Diagnostics.EventSeverity.Info,
            $"Ritual loot quiet at ({anchor.X},{anchor.Y}); continuing altar chain",
            new Dictionary<string, object?>
            {
                ["gridX"] = anchor.X,
                ["gridY"] = anchor.Y,
                ["minimumSettleSeconds"] = RitualLootMinimumSettleSeconds,
                ["quietSeconds"] = RitualLootQuietSeconds,
            });
        _ritualLootAnchor = null;
        _ritualLootQuietSince = null;
        return BehaviorStatus.Running;
    }

    private EntityCache.Entry? ClosestRitualThreat(BehaviorContext ctx, float maxAnchorDistance)
    {
        if (ctx.Entities is null || ctx.Live is null) return null;
        var active = ClosestMechanic(ctx, MechanicKind.RitualRune, MechanicStatus.Active);
        if (active is null) return null;
        var player = ctx.Live.Value.GridPosition;
        EntityCache.Entry? best = null, bestBlacklisted = null;
        long bestD2 = long.MaxValue, bestBlacklistedD2 = long.MaxValue;
        foreach (var entry in ctx.Entities.Entries.Values)
        {
            if (!TargetEligibility.IsEligible(entry)) continue;
            long ax = entry.GridPosition.X - active.GridPosition.X;
            long ay = entry.GridPosition.Y - active.GridPosition.Y;
            if (ax * ax + ay * ay > maxAnchorDistance * maxAnchorDistance) continue;
            long px = entry.GridPosition.X - player.X;
            long py = entry.GridPosition.Y - player.Y;
            var d2 = px * px + py * py;
            // A ritual round cannot end while any spawned monster lives, so the damage-
            // evidence blacklist is only a PREFERENCE here, not a veto. A totem shot at
            // during its invulnerable spawn window soaks 700 ms of no-damage evidence,
            // gets blacklisted, and — as a hard filter — stalled the round (live report
            // 2026-07-15). If blacklisted targets are all that remain, retry them; the
            // evidence system re-blacklists if they're still immune.
            if (IsBlacklisted(entry.Id))
            {
                if (d2 < bestBlacklistedD2) { bestBlacklistedD2 = d2; bestBlacklisted = entry; }
                continue;
            }
            if (d2 < bestD2) { bestD2 = d2; best = entry; }
        }
        return best ?? bestBlacklisted;
    }

    private BehaviorStatus RitualRetreatTick(BehaviorContext ctx, Vector2i anchor)
    {
        var away = RetreatPoint(ctx);
        if (away is { } goal)
        {
            float dx = goal.X - anchor.X, dy = goal.Y - anchor.Y;
            var distance = MathF.Sqrt(dx * dx + dy * dy);
            if (distance > RitualLeashRadius && distance > 0.1f)
                goal = new Vector2i
                {
                    X = anchor.X + (int)(dx / distance * RitualLeashRadius),
                    Y = anchor.Y + (int)(dy / distance * RitualLeashRadius),
                };
            _movement.WalkToward(goal, new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live));
        }
        else _movement.Halt(new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live));
        TapBiggestThreat(ctx);
        return BehaviorStatus.Running;
    }

    private void UpdateMechanicEvents(BehaviorContext ctx)
    {
        if (ctx.Entities is null) return;
        foreach (var mechanic in new MechanicsView(ctx.Entities).Entries)
        {
            if (!_mechanicStatuses.TryGetValue(mechanic.Id, out var prior))
            {
                _mechanicStatuses[mechanic.Id] = mechanic.Status;
                Diagnostics.EventLog.Emit("mechanic", "mechanic.discovered", Diagnostics.EventSeverity.Info,
                    $"{mechanic.Kind} {mechanic.Id} observed as {mechanic.Status}", MechanicEventData(mechanic, null));
                continue;
            }
            // Completed Rituals are terminal for the lifetime of this map — but ONLY if we
            // actually saw them Active. State reads flicker (live 2026-07-15: a rune the bot
            // never fought latched Completed, the chain skipped it, and the shop's
            // all-complete gate passed with a ritual left standing). Once their entity
            // leaves the network bubble the raw StateMachine fields regress to Unknown;
            // retain the verified completion instead of revisiting the altar.
            if (mechanic.Kind == MechanicKind.RitualRune
                && prior == MechanicStatus.Completed
                && _ritualsFought.Contains(mechanic.Id)
                && mechanic.Status != MechanicStatus.Completed)
                continue;
            if (mechanic.Kind == MechanicKind.RitualRune
                && mechanic.Status == MechanicStatus.Active)
                _ritualsFought.Add(mechanic.Id);
            if (prior == mechanic.Status) continue;
            _mechanicStatuses[mechanic.Id] = mechanic.Status;
            Diagnostics.EventLog.Emit("mechanic", "mechanic.state-changed", Diagnostics.EventSeverity.Info,
                $"{mechanic.Kind} {mechanic.Id}: {prior} -> {mechanic.Status}", MechanicEventData(mechanic, prior));
            if (mechanic.Kind == MechanicKind.RitualRune
                && prior == MechanicStatus.Active
                && mechanic.Status == MechanicStatus.Completed)
            {
                _ritualLootAnchor = mechanic.GridPosition;
                _ritualLootMinimumUntil = BotMonotonicClock.Now.Add(
                    TimeSpan.FromSeconds(RitualLootMinimumSettleSeconds));
                _ritualLootQuietSince = null;
                Diagnostics.EventLog.Emit(
                    "ritual", "ritual.loot-settle-started", Diagnostics.EventSeverity.Info,
                    $"Ritual {mechanic.Id} completed; holding for drops before next altar",
                    new Dictionary<string, object?>
                    {
                        ["altarId"] = mechanic.Id,
                        ["gridX"] = mechanic.GridPosition.X,
                        ["gridY"] = mechanic.GridPosition.Y,
                    });
            }
        }
    }

    private static IReadOnlyDictionary<string, object?> MechanicEventData(
        MechanicEntry mechanic, MechanicStatus? prior) => new Dictionary<string, object?>
    {
        ["id"] = mechanic.Id,
        ["kind"] = mechanic.Kind.ToString(),
        ["prior"] = prior?.ToString(),
        ["status"] = mechanic.Status.ToString(),
        ["gridX"] = mechanic.GridPosition.X,
        ["gridY"] = mechanic.GridPosition.Y,
        ["path"] = mechanic.Path,
        ["shrineAvailable"] = mechanic.Entry.ShrineAvailable.Truth.ToString(),
        ["ritualCurrentStateKnown"] = mechanic.Entry.RitualCurrentState.IsKnown,
        ["ritualCurrentState"] = mechanic.Entry.RitualCurrentState.Value,
        ["ritualInteractionEnabledKnown"] = mechanic.Entry.RitualInteractionEnabled.IsKnown,
        ["ritualInteractionEnabled"] = mechanic.Entry.RitualInteractionEnabled.Value,
    };

    /// <summary>Tears the bot already used or gave up on this map. A used tear VANISHES,
    /// leaving a stale cache entry whose last-known Targetable=true reads Available forever —
    /// without this set the sweep re-magnetizes to the ghost and oscillates in place
    /// (live 2026-07-15).</summary>
    private readonly HashSet<uint> _memoryTearsResolved = new();

    private MechanicEntry? NextMemoryTear(BehaviorContext ctx)
    {
        if (ctx.Entities is null || ctx.Live is not { } live) return null;
        MechanicEntry? best = null;
        long bestD2 = long.MaxValue;
        foreach (var m in new MechanicsView(ctx.Entities).Entries)
        {
            if (m.Kind != MechanicKind.MemoryTear || !m.IsAvailable) continue;
            if (m.Entry.IsStale || _memoryTearsResolved.Contains(m.Id)) continue;
            long dx = m.GridPosition.X - live.GridPosition.X;
            long dy = m.GridPosition.Y - live.GridPosition.Y;
            var d2 = dx * dx + dy * dy;
            if (d2 < bestD2) { bestD2 = d2; best = m; }
        }
        return best;
    }

    private BehaviorStatus MemoryTearTick(BehaviorContext ctx)
    {
        var target = NextMemoryTear(ctx);
        if (target is null) return BehaviorStatus.Failure;
        var status = _takeMemoryTear.Tick(ctx);
        // Any terminal outcome resolves the tear for this map: Success = clicked and it
        // vanished; Failure = max attempts / no click point / no path. One shot each —
        // an unresolved terminal state must never keep pulling the sweep back.
        if (status != BehaviorStatus.Running)
            _memoryTearsResolved.Add(target.Id);
        return status;
    }

    private MechanicEntry? ClosestMechanic(
        BehaviorContext ctx, MechanicKind kind, MechanicStatus status)
    {
        if (ctx.Entities is null || ctx.Live is null) return null;
        var from = ctx.Live.Value.GridPosition;
        MechanicEntry? best = null;
        long bestD2 = long.MaxValue;
        foreach (var mechanic in new MechanicsView(ctx.Entities).Entries)
        {
            if (mechanic.Kind != kind || EffectiveMechanicStatus(mechanic) != status) continue;
            long dx = mechanic.GridPosition.X - from.X;
            long dy = mechanic.GridPosition.Y - from.Y;
            var d2 = dx * dx + dy * dy;
            if (d2 < bestD2) { bestD2 = d2; best = mechanic; }
        }
        return best;
    }

    private MechanicStatus MechanicStatusOf(BehaviorContext ctx, EntityCache.Entry entry)
    {
        if (ctx.Entities is null) return MechanicStatus.Unknown;
        var mechanic = new MechanicsView(ctx.Entities).Entries
            .FirstOrDefault(item => item.Id == entry.Id);
        return mechanic is null ? MechanicStatus.Unknown : EffectiveMechanicStatus(mechanic);
    }

    /// <summary>Rituals we observed in the Active state — the precondition for trusting a
    /// later Completed read as terminal (a flickered state=3 on an unfought rune must not
    /// latch it done).</summary>
    private readonly HashSet<uint> _ritualsFought = new();

    private MechanicStatus EffectiveMechanicStatus(MechanicEntry mechanic)
        => mechanic.Kind == MechanicKind.RitualRune
            && _ritualsFought.Contains(mechanic.Id)
            && _mechanicStatuses.TryGetValue(mechanic.Id, out var verified)
            && verified == MechanicStatus.Completed
                ? MechanicStatus.Completed
                : mechanic.Status;

    private sealed record MechanicCounts(
        int shrinesSeen,
        int shrinesAvailable,
        int altarsSeen,
        int altarsAvailable,
        int altarsTaken,
        int ritualFresh,
        int ritualActive,
        int ritualComplete,
        int ritualPrioritySeen,
        int ritualPriorityDead,
        bool ritualOrderFrozen,
        string ritualScores);

    private MechanicCounts MechanicTelemetry(BehaviorContext ctx)
    {
        var entries = ctx.Entities is null
            ? Array.Empty<MechanicEntry>()
            : new MechanicsView(ctx.Entities).Entries.ToArray();
        return new MechanicCounts(
            entries.Count(e => e.Kind == MechanicKind.Shrine),
            entries.Count(e => e.Kind == MechanicKind.Shrine && e.IsAvailable),
            entries.Count(e => e.Kind == MechanicKind.EldritchAltar),
            entries.Count(e => e.Kind == MechanicKind.EldritchAltar && e.IsAvailable
                && !EldritchAltarLedger.IsResolved(ctx.Snapshot.AreaHash, e.Id)),
            EldritchAltarLedger.CountTaken(ctx.Snapshot.AreaHash),
            entries.Count(e => e.Kind == MechanicKind.RitualRune && EffectiveMechanicStatus(e) == MechanicStatus.Available),
            entries.Count(e => e.Kind == MechanicKind.RitualRune && EffectiveMechanicStatus(e) == MechanicStatus.Active),
            entries.Count(e => e.Kind == MechanicKind.RitualRune && EffectiveMechanicStatus(e) == MechanicStatus.Completed),
            _ritualPriority.PrioritySeen,
            _ritualPriority.PriorityDead,
            _ritualPriority.IsFrozen,
            string.Join(",", _ritualPriority.Scores
                .OrderByDescending(pair => pair.Value)
                .Select(pair => $"{pair.Key}:{pair.Value}")));
    }

    private static float Distance(Vector2i a, Vector2i b)
    {
        var dx = (float)(a.X - b.X);
        var dy = (float)(a.Y - b.Y);
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    // ── Postures — delegated to the shared CombatCoordinator ────────────────

    /// <summary>Default: move along the frontier, tap the biggest threat (Attack builds), and
    /// keep Penance Mark / a curse on rare+ (aura builds). Same brain as Simulacrum.</summary>
    private BehaviorStatus PushTick(BehaviorContext ctx)
    {
        _explore.Tick(ctx);            // drives forward movement (side-effect); status ignored
        _coord.TapBiggestThreat(ctx);  // opportunistic attack while moving
        _coord.MarkTick(ctx);          // Penance Mark / curse on rare+ to grow density
        return BehaviorStatus.Running;
    }

    private BehaviorStatus RetreatTick(BehaviorContext ctx) => _coord.RetreatTick(ctx);
    private BehaviorStatus UnloadTick(BehaviorContext ctx) => _coord.UnloadTick(ctx);
    private void TapBiggestThreat(BehaviorContext ctx) => _coord.TapBiggestThreat(ctx);
    private void TapThreat(BehaviorContext ctx, EntityCache.Entry target, string intent)
        => _coord.TapThreat(ctx, target, intent);
    private bool IsBlacklisted(uint id) => _coord.IsBlacklisted(id);
    private Vector2i? RetreatPoint(BehaviorContext ctx) => _coord.RetreatPoint(ctx);
    private static bool LowHp(BehaviorContext ctx) => CombatCoordinator.LowHp(ctx);
    private bool ShouldUnload(BehaviorContext ctx) => _coord.ShouldUnload(ctx);
    private string Posture(BehaviorContext ctx) => _coord.Posture(ctx);
}
