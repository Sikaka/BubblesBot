using BubblesBot.Bot.Behaviors;
using BubblesBot.Bot.Behaviors.Combat;
using BubblesBot.Bot.Behaviors.Interact;
using BubblesBot.Bot.Behaviors.Loot;
using BubblesBot.Bot.Behaviors.Movement;
using BubblesBot.Bot.Input;
using BubblesBot.Bot.Settings;
using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;
using BubblesBot.Core.Knowledge;
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
    private readonly BossCheckpointPortalSystem _bossCheckpointPortal;
    private readonly FollowPath          _bossCheckpointApproach;
    private readonly FollowPath          _bossArenaSearch;
    private readonly FollowPath          _bossArenaInward;
    private readonly FollowPath          _visibleBossApproach;
    private readonly FollowPath          _chimeraRevealApproach;
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
    private TimeSpan? _bossClearCandidateSince;
    private readonly HashSet<uint> _deliriumDriveBySkipped = new();
    private TimeSpan _deliriumPackEnteredAt = TimeSpan.MinValue;
    private Vector2i? _deliriumPackAnchor;
    private bool _bossArenaEntered;
    private Vector2i? _bossCheckpointGoal;
    private bool _bossCheckpointRecoveryPending;
    private string? _requestedAbandonReason;
    private Vector2i? _bossArenaSearchGoal;
    private bool _bossArenaLandmarkRejected;
    private Vector2i? _bossArenaInwardGoal;
    private Vector2i _bossArenaEntryGrid;
    private int _bossArenaInwardAttempt;
    private Vector2i? _visibleBossGoal;
    private TimeSpan _lastVisibleBossUnreachableAt = TimeSpan.MinValue;
    private Vector2i? _chimeraRevealAnchor;
    private Vector2i? _chimeraRevealGoal;
    private int _chimeraRevealAttempt;
    private bool _chimeraRevealGoalIsCloud;
    private readonly HashSet<Vector2i> _chimeraCloudsVisited = new();
    private Vector2i? _persistentTraversalOrigin;
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
    private const float BossCheckpointStagingDistanceGrid = 32f;
    private const float BossCheckpointSafetyRadiusGrid = 55f;
    private const double BossClearSettleSeconds = 8.0;

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
    public bool BossCheckpointPortalReady => _bossCheckpointPortal.IsReady;
    public string? RequestedAbandonReason => _requestedAbandonReason;
    public Vector2i? TraversalOrigin => _persistentTraversalOrigin;

    public void SetTraversalOrigin(Vector2i? origin)
        => _persistentTraversalOrigin = origin;

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
            getSnapshot, TransitionEligible,
            fallbackGrid: _ => _transitGoal,
            fallbackLabelFilter: IsArenaLabel);
        _completedBossArenaExit = new EnterAreaTransition(
            "completed boss arena exit", _interact, _movement, _skills,
            getSnapshot, CompletedBossArenaExitTransitionEligible,
            // Arena exits are local doors. Letting A* invent a Blink Arrow gap step caused an
            // endless cast loop 52 grids from a fresh Strand exit; route around on foot.
            allowGapCrossing: false);
        _bossCheckpointPortal = new BossCheckpointPortalSystem(
            _movement, () => _settings.Current.StackedDeckPortalKeyVk, getEntities, getLive);
        _bossCheckpointApproach = new FollowPath(
            "stage for boss checkpoint portal", _movement,
            _ => _bossCheckpointGoal, _skills,
            goalArrivalRadius: BossCheckpointStagingDistanceGrid,
            allowGapCrossing: false);
        _bossArenaSearch = new FollowPath("seek boss arena", _movement,
            _ => _bossArenaSearchGoal, _skills, goalArrivalRadius: 18f);
        _bossArenaInward = new FollowPath("stage inside boss arena", _movement,
            _ => _bossArenaInwardGoal, _skills, goalArrivalRadius: 16f,
            allowGapCrossing: false);
        _visibleBossApproach = new FollowPath("approach required boss", _movement,
            _ => _visibleBossGoal, _skills,
            goalArrivalRadiusProvider: ctx => Math.Max(24f, ctx.Settings.ProximityHoldRadiusGrid * 0.75f));
        _chimeraRevealApproach = new FollowPath("reveal dormant Chimera", _movement,
            _ => _chimeraRevealGoal, _skills,
            goalArrivalRadius: 5f,
            allowGapCrossing: false);
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
        _delirium = new DeliriumController(_movement, _skills, _loot, getSnapshot);
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
            // Flicker Strike must remain held through ordinary packs, boss phases, and low-HP
            // pressure: attacking is what teleports/leaches. Generic retreat would cancel the
            // build's survival loop and strand it among Chimera adds.
            new If("flicker engage", ShouldEngageFlicker,
                new Behaviors.Action("hold Flicker Strike", FlickerEngageTick)),
            new If("low HP", LowHp, new Behaviors.Action("retreat", RetreatTick)),
            // A ranged character must stop and establish fire before any optional world
            // interaction can pull it through a pack. Modal encounter handling and the HP
            // retreat stay above this; loot, shrines, altars, and exploration stay below it.
            new If("ranged engage",
                ShouldEngageRanged,
                new Behaviors.Action("ranged standoff", RangedEngageTick)),
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
            new If("place boss checkpoint portal", ShouldPlaceBossCheckpointPortal,
                new Behaviors.Action("boss checkpoint portal", BossCheckpointPortalTick)),
            new If("enter required boss arena", ShouldEnterRequiredBossArena,
                new Behaviors.Action("boss arena transition", BossArenaTransitionTick)),
            // Chimera's smoke phase leaves the living boss entity fresh but Dormant. Once the
            // add wave is actually gone, walking through his current position reveals him.
            // Active adds always retain priority, preventing the dormant actor from stealing
            // combat control during the preceding wave.
            new If("reveal dormant Chimera", ShouldRevealDormantChimera,
                new Behaviors.Action("Chimera smoke search", ChimeraRevealTick)),
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
            // Finish Delirium while we are still in the boss room: click End, hold the delayed
            // reward barrier, and let the higher-priority local loot branch drain the drops.
            // Only then may the arena-exit branch below return to the parent map.
            new If("Delirium finalization",
                ctx => _delirium.ShouldFinalize(ctx, ObjectivesComplete(ctx)),
                new Behaviors.Action("end Delirium and settle rewards", _delirium.FinalizeTick)),
            // Boss and Delirium loot have already had first refusal above. Once the configured
            // roster is dead and Delirium is settled, take the arena exit instead of exploring
            // this small, disconnected sub-area as though it were the parent map.
            new If("exit completed boss arena", ShouldExitCompletedBossArena,
                new Behaviors.Action("completed boss arena exit", CompletedBossArenaExitTick)),
            // Late in a separate-arena map, the tile landmark is a better objective than
            // leftover coverage. Walking to it streams the actual transition entity, after
            // which the branch above owns navigation and the verified click.
            new If("seek required boss arena", ShouldSeekRequiredBossArena,
                new Behaviors.Action("boss arena search", BossArenaSearchTick)),
            // Only exploration/backtracking is paced. Combat, incidental mechanics, and local
            // loot above this branch can always preempt the hold.
            new If("Delirium fog-front hold", _delirium.ShouldThrottle,
                new Behaviors.Action("wait for Delirium fog front", _delirium.ThrottleTick)),
            new If("refresh ritual state", ShouldRefreshRitual,
                new Behaviors.Action("ritual refresh", RefreshRitualTick)),
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
    private bool _rangedEngagedThisTick;

    private BehaviorStatus ProximityEngageTick(BehaviorContext ctx)
        => _coord.ProximityEngageTick(ctx, _delirium.IsEncounterActive, DeliriumTargetSkipped);

    private BehaviorStatus RangedEngageTick(BehaviorContext ctx)
    {
        _rangedEngagedThisTick = true;
        return _coord.RangedEngageTick(ctx, DeliriumTargetSkipped);
    }

    private bool ShouldEngageRanged(BehaviorContext ctx)
        => ctx.Settings.MapClearStance == 2
           && (_coord.RangedRepositionActive
               || _coord.SelectRangedTarget(ctx, DeliriumTargetSkipped) is not null);

    private bool ShouldEngageFlicker(BehaviorContext ctx)
        => _coord.SelectFlickerTarget(ctx) is not null;

    private BehaviorStatus FlickerEngageTick(BehaviorContext ctx)
        => _coord.FlickerEngageTick(ctx);

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
    /// and blacklisted so the sweep stops selecting it. End-of-map completion uses that same
    /// actionable-label contract, so an unreachable drop never triggers a backtracking pass or
    /// holds a completed map open until the WorldItem despawns.
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
        var mapName = ctx.Strategy?.Supply.Map.TargetMapName ?? "";
        var bossRequired = ctx.Strategy?.Completion.RequireBossKill == true;
        var canComplete = MapZoneCompletionPolicy.CanCompleteMap(
            ExplorationDone(ctx), bossRequired, BossComplete(ctx), _delirium.IsSettled,
            BubblesBot.Core.Knowledge.MapBossCatalog.BossCompletesTraversal(mapName));
        if (MapZoneCompletionPolicy.ShouldCompleteImmediately(
                canComplete,
                BubblesBot.Core.Knowledge.MapBossCatalog.HasSeparateBossArena(mapName),
                _bossArenaEntered))
        {
            _exhaustedSince = null;
            return true;
        }

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
        && (ctx.Strategy?.Completion.RequireBossKill != true || BossComplete(ctx));

    private bool TraversalDone(BehaviorContext ctx)
    {
        var mapName = ctx.Strategy?.Supply.Map.TargetMapName ?? "";
        var bossRequired = ctx.Strategy?.Completion.RequireBossKill == true;
        if (ExplorationDone(ctx))
        {
            if (MapZoneCompletionPolicy.ShouldContinueBossDiscovery(
                    bossRequired,
                    BossComplete(ctx),
                    BubblesBot.Core.Knowledge.MapBossCatalog.HasSeparateBossArena(mapName),
                    _bossArenaEntered,
                    _exploration.IsExhausted,
                    FindEligibleTransition(ctx) is not null))
                return false;
            return true;
        }
        return bossRequired
            && BossComplete(ctx)
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
        if (!MapTransitionPolicy.IsTraversalCandidate(e)) return false;
        if (_bossCheckpointRecoveryPending
            && _bossCheckpointGoal is { } checkpointDoor
            && Math.Abs(checkpointDoor.X - e.GridPosition.X)
               + Math.Abs(checkpointDoor.Y - e.GridPosition.Y) < 20)
            return true;
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
        if (!MapTransitionPolicy.IsTraversalCandidate(e)) return false;
        foreach (var (area, pos) in _usedTransitions)
            if (area == _currentArea
                && Math.Abs(pos.X - e.GridPosition.X) + Math.Abs(pos.Y - e.GridPosition.Y) < 20)
                return false;
        return true;
    }

    private bool ShouldPlaceBossCheckpointPortal(BehaviorContext ctx)
    {
        if (!_orchestrated || _bossCheckpointPortal.IsReady || _bossCheckpointPortal.IsFailed)
            return false;
        if (ctx.Strategy?.Completion.RequireBossKill != true
            || BossComplete(ctx)
            || _bossArenaEntered
            || ctx.Live is not { } live)
            return false;

        var mapName = ctx.Strategy.Supply.Map.TargetMapName;
        if (!BubblesBot.Core.Knowledge.MapBossCatalog.HasSeparateBossArena(mapName))
            return false;

        if (_bossCheckpointGoal is null)
        {
            var transition = FindEligibleTransition(ctx);
            if (transition is null) return false;
            _bossCheckpointGoal = transition.GridPosition;
        }

        // Never trade safety for checkpoint efficiency. Ordinary combat gets control until
        // the staging pocket is quiet, then the portal branch resumes from its latched door.
        if (HasLivingHostileWithin(ctx, BossCheckpointSafetyRadiusGrid))
            return false;

        return true;
    }

    private BehaviorStatus BossCheckpointPortalTick(BehaviorContext ctx)
    {
        if (_bossCheckpointGoal is null || ctx.Live is not { } live)
            return BehaviorStatus.Failure;

        if (Distance(live.GridPosition, _bossCheckpointGoal.Value)
            > BossCheckpointStagingDistanceGrid)
            return _bossCheckpointApproach.Tick(ctx);

        return _bossCheckpointPortal.Tick(ctx);
    }

    private static bool HasLivingHostileWithin(BehaviorContext ctx, float range)
    {
        if (ctx.Entities is null || ctx.Live is not { } live) return false;
        var r2 = range * range;
        foreach (var entry in ctx.Entities.Entries.Values)
        {
            if (entry.IsStale
                || entry.Kind != EntityListReader.EntityKind.Monster
                || entry.Disposition != EntityDisposition.Combatant
                || entry.AlliedReaction.Truth == ObservationTruth.True
                || entry.Dormancy.Truth == ObservationTruth.True)
                continue;
            // This is a positive living-hostile gate. An unreadable life component is not
            // evidence that an actor is alive; synthetic/dehydrating boss actors commonly
            // lose that component after death and otherwise block completion forever.
            if (entry.LifeReadable.Truth != ObservationTruth.True
                || entry.HpCurrent <= 0
                || entry.HpMax <= 1)
                continue;
            if (DistanceSquared(entry.GridPosition, live.GridPosition) <= r2)
                return true;
        }
        return false;
    }

    /// <summary>
    /// A raw boss disappearance is only a candidate completion. Phased encounters can remove
    /// the boss entity between waves, and the entity scanner correctly reports the same shape
    /// as death for that short interval. Require a quiet local arena after the death evidence;
    /// any living boss or add immediately revokes the candidate. This also gives delayed phase
    /// spawns time to appear before terminal-map completion can leave through a portal.
    /// </summary>
    private bool BossComplete(BehaviorContext ctx)
    {
        var mapName = ctx.Strategy?.Supply.Map.TargetMapName ?? "";
        if (!_bossTracker.IsComplete
            || HasFreshLivingRequiredBossEntity(ctx, mapName)
            || HasLivingHostileWithin(ctx, 300f))
        {
            _bossClearCandidateSince = null;
            return false;
        }

        _bossClearCandidateSince ??= BotMonotonicClock.Now;
        return (BotMonotonicClock.Now - _bossClearCandidateSince.Value).TotalSeconds
            >= BossClearSettleSeconds;
    }

    private bool ShouldEnterRequiredBossArena(BehaviorContext ctx)
    {
        if (ctx.Strategy?.Completion.RequireBossKill != true) return false;
        var mapName = ctx.Strategy.Supply.Map.TargetMapName;
        // Nearby positive boss identity means we are already inside the arena. The distance
        // gate matters for same-hash arenas: parent-zone scans can retain a remote boss corpse,
        // which must not reconstruct the arena latch after we have exited.
        if (HasFreshLivingRequiredBossEntity(ctx, mapName))
        {
            _bossArenaEntered = true;
            if (_bossArenaEntryGrid.X == 0 && _bossArenaEntryGrid.Y == 0
                && ctx.Live is { } live)
                _bossArenaEntryGrid = live.GridPosition;
            return false;
        }
        if (BossComplete(ctx)) return false;
        if (_bossArenaEntered) return false;
        var hasSeparateArena = BubblesBot.Core.Knowledge.MapBossCatalog.HasSeparateBossArena(mapName);
        var hasTerminalEndpoint = BubblesBot.Core.Knowledge.MapBossCatalog.BossCompletesTraversal(mapName);
        if (!hasSeparateArena && !hasTerminalEndpoint)
            return false;
        if (hasSeparateArena
            && _orchestrated
            && !_bossCheckpointPortal.IsReady
            && !_bossCheckpointPortal.IsFailed)
            return false;
        // Once the entry behavior owns a door, keep that exact parent-zone coordinate
        // latched through same-hash teleport confirmation. Re-resolving here after the
        // displacement would select the nearby arena exit and incorrectly mark it used.
        if (_transitGoal is not null) return true;
        var transition = FindEligibleTransition(ctx);
        var arenaLabel = FindVisibleArenaLabel(ctx);
        if (transition is null && arenaLabel is null) return false;
        // EnterAreaTransition accepts stale transition coordinates as navigation anchors and
        // requires a fresh ground label only for the click. Once the known boss door has been
        // observed, it beats every remaining coverage frontier regardless of current distance.
        _transitGoal = transition?.GridPosition
            ?? arenaLabel!.EntityGridPosition
            ?? ctx.Live?.GridPosition;
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
            && entry.HpMax > 1
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
        var connectedTerminal =
            BubblesBot.Core.Knowledge.MapBossCatalog.BossCompletesTraversal(mapName)
            && !BubblesBot.Core.Knowledge.MapBossCatalog.HasSeparateBossArena(mapName);
        // Guardian layouts are linear rush maps. Their physical endpoint is the primary
        // traversal objective from spawn; combat/loot above the route still gets first
        // refusal. Do not gate this on reveal percentage: network-bubble coverage and
        // process recovery can both say nothing useful about whether the endpoint was reached.
        if (connectedTerminal
            && ctx.Strategy?.Completion.RequireBossKill == true
            && !BossComplete(ctx)
            && !_bossArenaEntered)
            return true;

        var (revealed, total) = _exploration.Progress(ctx);
        var revealPercent = total > 0 ? 100.0 * revealed / total : 0;
        // Some terminal boss rooms (the Shaper Guardians) are connected terrain rather
        // than a real AreaTransition. They still need the same late-map landmark priority:
        // after a death/restart, ordinary coverage can exhaust far from the already-revealed
        // arena and otherwise leave the required boss permanently undiscovered.
        var hasDedicatedBossDestination =
            BubblesBot.Core.Knowledge.MapBossCatalog.HasSeparateBossArena(mapName)
            || BubblesBot.Core.Knowledge.MapBossCatalog.BossCompletesTraversal(mapName);
        return MapZoneCompletionPolicy.ShouldPrioritizeBossArena(
            ctx.Strategy?.Completion.RequireBossKill == true,
            BossComplete(ctx),
            hasDedicatedBossDestination,
            _bossArenaEntered,
           revealPercent);
    }

    private bool ShouldSeekRequiredBossArena(BehaviorContext ctx)
    {
        _bossArenaSearchGoal = null;
        if (!BossHuntActive(ctx) || ctx.Live is not { } live)
            return false;
        var mapName = ctx.Strategy?.Supply.Map.TargetMapName ?? "";
        var connectedTerminal = BubblesBot.Core.Knowledge.MapBossCatalog.BossCompletesTraversal(mapName)
            && !BubblesBot.Core.Knowledge.MapBossCatalog.HasSeparateBossArena(mapName);
        if (_bossArenaLandmarkRejected && !connectedTerminal) return false;
        // A real separate arena is owned by the verified transition branch. Connected
        // Guardian maps also contain ordinary corridor transitions, but those must not
        // suppress their terminal boss-landmark route.
        if (BubblesBot.Core.Knowledge.MapBossCatalog.HasSeparateBossArena(mapName)
            && FindEligibleTransition(ctx) is not null)
            return false;
        _bossArenaSearchGoal = ctx.Snapshot.TileMap.FindNearestLandmark(
            LandmarkCatalog.Kind.BossArena, live.GridPosition);
        if (_bossArenaSearchGoal is null && connectedTerminal)
            _bossArenaSearchGoal = _exploration.FarthestConnectedPoint(ctx, _arrivalGrid);
        return _bossArenaSearchGoal is not null;
    }

    private static GroundLabelView? FindVisibleArenaLabel(BehaviorContext ctx)
        => ctx.Snapshot.GroundLabels
            .Where(label => label.IsRectOnScreen && IsArenaLabel(label))
            .OrderBy(label => label.DistanceToPlayer)
            .FirstOrDefault();

    internal static bool IsArenaLabel(GroundLabelView label)
        => IsArenaLabelText(label.DisplayName)
        || IsArenaLabelText(label.RenderName)
        || label.Path.Contains("Arena", StringComparison.OrdinalIgnoreCase);

    internal static bool IsArenaLabelText(string text)
        => text.Trim().Equals("Arena", StringComparison.OrdinalIgnoreCase);

    private BehaviorStatus BossArenaSearchTick(BehaviorContext ctx)
    {
        var status = _bossArenaSearch.Tick(ctx);
        if (status == BehaviorStatus.Success)
        {
            var mapName = ctx.Strategy?.Supply.Map.TargetMapName ?? "";
            if (BubblesBot.Core.Knowledge.MapBossCatalog.BossCompletesTraversal(mapName)
                && !BubblesBot.Core.Knowledge.MapBossCatalog.HasSeparateBossArena(mapName))
            {
                // Connected Guardian arenas have no transition to click. Reaching their
                // landmark is the desired result; hold this branch until proximity streams
                // or activates the exact boss entity, which then preempts us above.
                return BehaviorStatus.Running;
            }
            if (FindEligibleTransition(ctx) is not null)
                return status;
            _bossArenaLandmarkRejected = true;
            Diagnostics.EventLog.Emit(
                "maploop", "maploop.boss-arena-landmark-empty",
                Diagnostics.EventSeverity.Warning,
                $"boss-arena landmark {_bossArenaSearchGoal} reached without a valid door; resuming connected coverage");
            _bossArenaSearchGoal = null;
            _bossArenaSearch.Reset();
            return BehaviorStatus.Failure;
        }
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
        _bossCheckpointRecoveryPending = false;
        _bossArenaLandmarkRejected = false;
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
                BossComplete(ctx),
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
            || BossComplete(ctx)
            || ctx.Entities is null
            || ctx.Live is not { } live)
            return false;

        var mapName = ctx.Strategy.Supply.Map.TargetMapName;
        var fragments = BubblesBot.Core.Knowledge.MapBossCatalog.BossFragments(mapName);
        if (fragments.Count == 0) return false;
        var chimera = mapName.Contains("Chimera", StringComparison.OrdinalIgnoreCase);

        EntityCache.Entry? closest = null;
        var closestD2 = float.PositiveInfinity;
        foreach (var entry in ctx.Entities.Entries.Values)
        {
            if (!fragments.Any(fragment => entry.Path.Contains(
                    fragment, StringComparison.OrdinalIgnoreCase))) continue;
            // Chimera's dormant marker is not his location during smoke and must never
            // preempt active adds/cloud search. Other Guardians (live Phoenix) can initially
            // stream as dormant and require proximity before becoming combat-eligible, so
            // their positively identified living actor is a valid approach target.
            var dormantLivingGuardian = !chimera
                && !entry.IsStale
                && entry.LifeReadable.Truth == ObservationTruth.True
                && entry.HpCurrent > 0
                && entry.Dormancy.Truth == ObservationTruth.True;
            if (!TargetEligibility.IsEligible(entry) && !dormantLivingGuardian) continue;

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

    private bool ShouldRevealDormantChimera(BehaviorContext ctx)
    {
        var mapName = ctx.Strategy?.Supply.Map.TargetMapName ?? "";
        if (!mapName.Contains("Chimera", StringComparison.OrdinalIgnoreCase)
            || !_bossArenaEntered
            || BossComplete(ctx)
            || ctx.Entities is null)
        {
            ResetChimeraReveal();
            return false;
        }

        // A targetable boss or add owns Flicker/combat above this branch. The smoke search is
        // only the otherwise-idle gap between Chimera's add phase and his reappearance.
        if (ctx.Entities.Entries.Values.Any(entry => TargetEligibility.IsEligible(entry)))
        {
            ResetChimeraReveal();
            return false;
        }

        var fragments = BubblesBot.Core.Knowledge.MapBossCatalog.BossFragments(mapName);
        var dormant = ctx.Entities.Entries.Values.FirstOrDefault(entry =>
            !entry.IsStale
            && entry.LifeReadable.Truth == ObservationTruth.True
            && entry.HpCurrent > 0
            && entry.Dormancy.Truth == ObservationTruth.True
            && fragments.Any(fragment => entry.Path.Contains(
                fragment, StringComparison.OrdinalIgnoreCase)));
        if (dormant is null)
        {
            ResetChimeraReveal();
            return false;
        }

        // Smoke patches hydrate as paired client/server ground-effect entities. Prefer their
        // exact coordinates over the dormant boss marker: live capture proved the latter sits
        // on ArenaMiddle and is only a generic marker while Chimera is hidden.
        if (_chimeraRevealGoal is null && ctx.Live is { } live)
        {
            var cloud = ctx.Entities.Entries.Values
                .Where(entry => !entry.IsStale
                    && entry.Path.Contains(
                        "ground_effects/FillGroundEffect", StringComparison.OrdinalIgnoreCase)
                    && !_chimeraCloudsVisited.Any(visited =>
                        Distance(visited, entry.GridPosition) < 8f))
                .OrderBy(entry => Distance(live.GridPosition, entry.GridPosition))
                .FirstOrDefault();
            if (cloud is not null)
            {
                _chimeraRevealGoal = cloud.GridPosition;
                _chimeraRevealGoalIsCloud = true;
            }
        }

        if (_chimeraRevealGoal is null)
        {
            _chimeraRevealAnchor ??= ctx.Entities.Entries.Values
                .FirstOrDefault(entry => !entry.IsStale
                    && entry.Path.Contains("ArenaMiddle", StringComparison.OrdinalIgnoreCase))
                ?.GridPosition ?? dormant.GridPosition;
            _chimeraRevealGoal = ChimeraRevealGoal(
                _chimeraRevealAnchor.Value, _chimeraRevealAttempt);
            _chimeraRevealGoalIsCloud = false;
        }
        return true;
    }

    private BehaviorStatus ChimeraRevealTick(BehaviorContext ctx)
    {
        var status = _chimeraRevealApproach.Tick(ctx);
        if (status == BehaviorStatus.Running) return status;

        var completedGoal = _chimeraRevealGoal;
        Diagnostics.EventLog.Emit(
            "maploop", "maploop.chimera-smoke-search",
            status == BehaviorStatus.Success
                ? Diagnostics.EventSeverity.Info
                : Diagnostics.EventSeverity.Warning,
            $"Chimera smoke search point {_chimeraRevealAttempt + 1} " +
            $"{(status == BehaviorStatus.Success ? "crossed" : "unreachable")} at " +
            $"({completedGoal?.X},{completedGoal?.Y})" +
            (_chimeraRevealGoalIsCloud ? " [ground-effect cloud]" : " [arena circuit]"));
        if (_chimeraRevealGoalIsCloud && completedGoal is { } cloud)
            _chimeraCloudsVisited.Add(cloud);
        else
            _chimeraRevealAttempt = (_chimeraRevealAttempt + 1) % 17;
        _chimeraRevealGoal = null;
        _chimeraRevealGoalIsCloud = false;
        _chimeraRevealApproach.Reset();
        // Keep exclusive control until an active enemy appears; returning Success here lets
        // generic exhausted-map traversal briefly pull away from the smoke clouds.
        return BehaviorStatus.Running;
    }

    private void ResetChimeraReveal()
    {
        if (_chimeraRevealAnchor is null && _chimeraRevealGoal is null
            && _chimeraCloudsVisited.Count == 0) return;
        _chimeraRevealAnchor = null;
        _chimeraRevealGoal = null;
        _chimeraRevealAttempt = 0;
        _chimeraRevealGoalIsCloud = false;
        _chimeraCloudsVisited.Clear();
        _chimeraRevealApproach.Reset();
    }

    internal static Vector2i ChimeraRevealGoal(Vector2i anchor, int attempt)
    {
        if (attempt <= 0) return anchor;
        var index = (attempt - 1) % 8;
        var ring = (attempt - 1) / 8;
        var radius = 75 + ring * 55;
        (double X, double Y)[] directions =
        [
            (1, 0), (Math.Sqrt(0.5), Math.Sqrt(0.5)), (0, 1),
            (-Math.Sqrt(0.5), Math.Sqrt(0.5)), (-1, 0),
            (-Math.Sqrt(0.5), -Math.Sqrt(0.5)), (0, -1),
            (Math.Sqrt(0.5), -Math.Sqrt(0.5)),
        ];
        var direction = directions[index];
        return new Vector2i
        {
            X = anchor.X + (int)Math.Round(direction.X * radius),
            Y = anchor.Y + (int)Math.Round(direction.Y * radius),
        };
    }

    private BehaviorStatus VisibleBossApproachTick(BehaviorContext ctx)
    {
        var status = _visibleBossApproach.Tick(ctx);
        if (status == BehaviorStatus.Failure)
        {
            if (BotMonotonicClock.ElapsedSince(_lastVisibleBossUnreachableAt).TotalSeconds >= 5)
            {
                Diagnostics.EventLog.Emit(
                    "maploop", "maploop.visible-boss-unreachable",
                    Diagnostics.EventSeverity.Warning,
                    $"could not route to visible required boss at {_visibleBossGoal}");
                _lastVisibleBossUnreachableAt = BotMonotonicClock.Now;
            }
            _visibleBossGoal = null;
        }
        return status;
    }

    private bool ShouldStageInsideBossArena(BehaviorContext ctx)
    {
        if (ctx.Strategy?.Completion.RequireBossKill != true
            || !_bossArenaEntered
            || BossComplete(ctx)
            || _bossArenaInwardAttempt >= 8
            || ctx.Live is null)
            return false;

        var mapName = ctx.Strategy.Supply.Map.TargetMapName;
        var expectedBosses = BubblesBot.Core.Knowledge.MapBossCatalog
            .BossFragments(mapName).Count;
        if (!MapZoneCompletionPolicy.ShouldSearchArenaForMissingBoss(
                BossComplete(ctx), _bossTracker.BossesDead, expectedBosses))
            return false;
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
                _bossArenaInwardAttempt, 8, BossComplete(ctx)))
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
        var bossRequired = ctx.Strategy?.Completion.RequireBossKill == true;
        var bossCompletesTraversal = BubblesBot.Core.Knowledge.MapBossCatalog.BossCompletesTraversal(
            ctx.Strategy?.Supply.Map.TargetMapName ?? "");
        if (MapZoneCompletionPolicy.CanCompleteMap(
                ExplorationDone(ctx), bossRequired, BossComplete(ctx), _delirium.IsSettled,
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
        if (target is not null)
        {
            var mapName = ctx.Strategy?.Supply.Map.TargetMapName ?? string.Empty;
            // If checkpoint placement did not claim the final door, the exhausted-zone
            // fallback can still be the route into a known separate boss arena. Preserve
            // the same-hash displacement evidence and enable inward staging instead of
            // treating it as an ordinary zone hop.
            if (bossRequired && !BossComplete(ctx)
                && BubblesBot.Core.Knowledge.MapBossCatalog.HasSeparateBossArena(mapName))
                return BossArenaTransitionTick(ctx);
            return _transition.Tick(ctx);
        }

        if (!MapZoneCompletionPolicy.CanCompleteMap(
                ExplorationDone(ctx), bossRequired, BossComplete(ctx), _delirium.IsSettled,
                bossCompletesTraversal))
        {
            _movement.Halt(new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live));
            LastDecision = bossRequired && !BossComplete(ctx)
                ? "zone exhausted; waiting for required boss transition/evidence"
                : "zone exhausted; waiting for Delirium settlement";
            return BehaviorStatus.Running;
        }

        return BehaviorStatus.Running;
    }

    /// <summary>
    /// Drops transient corpse/combat/door state after checkpoint resurrection while preserving
    /// the current map's exploration, boss evidence, Delirium lifecycle, and loot ledger.
    /// Returns false unless this map positively confirmed its pre-boss portal.
    /// </summary>
    public bool PrepareForBossCheckpointRecovery()
    {
        if (!_bossCheckpointPortal.IsReady || _bossCheckpointGoal is null || IsCleared)
            return false;

        _movement.Release();
        _coord.ResetCombat();
        _interact.Cancel();
        _transition.Reset();
        _completedBossArenaExit.Reset();
        _bossCheckpointApproach.Reset();
        _bossArenaInward.Reset();
        _visibleBossApproach.Reset();
        _lootApproach.Reset();
        _bossArenaEntered = false;
        _bossCheckpointRecoveryPending = true;
        _transitGoal = null;
        _bossArenaInwardGoal = null;
        _bossArenaInwardAttempt = 0;
        _visibleBossGoal = null;
        _lastVisibleBossUnreachableAt = TimeSpan.MinValue;
        _chimeraRevealAnchor = null;
        _chimeraRevealGoal = null;
        _chimeraRevealAttempt = 0;
        _chimeraRevealGoalIsCloud = false;
        _chimeraCloudsVisited.Clear();
        _chimeraRevealApproach.Reset();
        _exhaustedSince = null;
        _root.Reset();
        LastDecision = "death recovery: returning to pre-boss checkpoint";
        return true;
    }

    /// <summary>Checkpoint resurrection can return to the entrance of the same map instance.
    /// Rebuild transient navigation and exploration state so a previously revealed map does
    /// not remain exhausted at the entrance.</summary>
    public void PrepareForSameInstanceCheckpointRecovery()
    {
        Reset();
        _exploration.Reset();
        _currentArea = 0;
        _usedTransitions.Clear();
        LastDecision = "death recovery: restarting same-instance map traversal";
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
        _bossCheckpointPortal.Reset();
        _bossCheckpointApproach.Reset();
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
        _bossClearCandidateSince = null;
        _deliriumDriveBySkipped.Clear();
        _deliriumPackEnteredAt = TimeSpan.MinValue;
        _deliriumPackAnchor = null;
        _bossArenaEntered = false;
        _bossCheckpointGoal = null;
        _bossCheckpointRecoveryPending = false;
        _requestedAbandonReason = null;
        _bossArenaSearchGoal = null;
        _bossArenaLandmarkRejected = false;
        _bossArenaInwardGoal = null;
        _bossArenaEntryGrid = default;
        _bossArenaInwardAttempt = 0;
        _visibleBossGoal = null;
        _lastVisibleBossUnreachableAt = TimeSpan.MinValue;
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
            _arrivalGrid = _persistentTraversalOrigin
                ?? ctx.Live?.GridPosition
                ?? default;
            _persistentTraversalOrigin ??= _arrivalGrid;
            _transitGoal = null;
            _bossArenaLandmarkRejected = false;
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

        // Propagate persistent-cover give-ups (position-keyed) into LootMemory so a bounded
        // mechanic cleanup never retries a drop we already proved unlootable.
        foreach (var spot in _loot.AbandonedSpots) _lootMemory.AbandonSpot(spot);
        _lootMemory.Track(ctx);  // bounded mechanic cleanup only; never a whole-map drain
        UpdateBossEvidence(ctx);
        _delirium.Observe(ctx);
        UpdateStackedDeckObservations(ctx);
        UpdateMechanicEvents(ctx);
        _coord.PreRoot(ctx);           // flasks + RF/douse pulse advance (shared)
        _rangedEngagedThisTick = false;
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

        var posture = _rangedEngagedThisTick ? "ranged" : _coord.Posture(ctx);
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
                trackerComplete = _bossTracker.IsComplete,
                complete = BossComplete(ctx),
                freshLivingBlocker = HasFreshLivingRequiredBossEntity(
                    ctx, ctx.Strategy?.Supply.Map.TargetMapName ?? ""),
                activeLivingBlocker = HasLivingHostileWithin(ctx, 300f),
                seen = _bossTracker.BossesSeen,
                dead = _bossTracker.BossesDead,
                huntActive = BossHuntActive(ctx),
                arenaEntered = _bossArenaEntered,
                traversalOrigin = _persistentTraversalOrigin is { } origin
                    ? new { x = origin.X, y = origin.Y }
                    : null,
                endpoint = _bossArenaSearchGoal is { } endpoint
                    ? new { x = endpoint.X, y = endpoint.Y }
                    : null,
                route = new
                {
                    decision = _bossArenaSearch.LastDecision,
                    index = _bossArenaSearch.CurrentPathIndex,
                    length = _bossArenaSearch.CurrentPath?.Count ?? 0,
                    goal = _bossArenaSearch.Goal is { } routeGoal
                        ? new { x = routeGoal.X, y = routeGoal.Y }
                        : null,
                },
                arenaLabel = ctx.Snapshot.GroundLabels
                    .Where(label => label.IsRectOnScreen)
                    .Select(label => new
                    {
                        display = label.DisplayName,
                        render = label.RenderName,
                        path = label.Path,
                        grid = label.EntityGridPosition is { } labelGrid
                            ? new { x = labelGrid.X, y = labelGrid.Y }
                            : null,
                    })
                    .FirstOrDefault(label =>
                        IsArenaLabelText(label.display)
                        || IsArenaLabelText(label.render)
                        || label.path.Contains("Arena", StringComparison.OrdinalIgnoreCase)),
                checkpointPortal = new
                {
                    ready = _bossCheckpointPortal.IsReady,
                    failed = _bossCheckpointPortal.IsFailed,
                    status = _bossCheckpointPortal.Status,
                    recoveryPending = _bossCheckpointRecoveryPending,
                    door = _bossCheckpointGoal is { } checkpoint
                        ? new { x = checkpoint.X, y = checkpoint.Y }
                        : null,
                },
                freshRequiredEntity = HasFreshRequiredBossEntity(
                    ctx, ctx.Strategy?.Supply.Map.TargetMapName ?? ""),
                chimeraReveal = _chimeraRevealGoal is { } reveal
                    ? new
                    {
                        attempt = _chimeraRevealAttempt + 1,
                        x = reveal.X,
                        y = reveal.Y,
                        exactCloud = _chimeraRevealGoalIsCloud,
                        cloudsVisited = _chimeraCloudsVisited.Count,
                    }
                    : null,
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
            _bossClearCandidateSince = null;
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
        if (!ZoneFinished(ctx)) return false;
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
