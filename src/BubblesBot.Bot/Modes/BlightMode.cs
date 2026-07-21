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
/// Blight encounter mode — first end-to-end "real farm" mode. Designed for builds that can
/// defend near the pump and perform lane-aware cleanup after the timer.
///
/// <code>
/// Selector "blight"
///   ├── If(low HP) → halt
///   ├── If(pump available) → walk + click pump
///   ├── If(encounter active &amp; not timed out) → anchor near pump + drive-by attack
///   ├── If(loot chests on &amp; chest available) → walk + click closest unopened blight chest
///   ├── If(loot in range) → halt + LootClosestVisible
///   └── Exit via portal (EnterAreaTransition picks the closest portal)
/// </code>
///
/// <para><b>Pump detection.</b> <see cref="MechanicsView"/> classifies the pump by path
/// (<c>EndsWith("/BlightPump")</c>). Activation flips to "non-targetable" after the encounter
/// starts — the same universal post-click signal shrines/altars use, so no state-machine
/// parsing is required for v1.</para>
///
/// <para><b>Defend phase.</b> Once the pump is taken (no longer in <c>Available</c>), we
/// flip to "anchor + attack." The bot walks back toward the pump position when it drifts past
/// <see cref="BotSettings.BlightDefendRadius"/>, and casts the attack skill on the closest
/// in-range hostile every tick. After <see cref="BotSettings.BlightDefendTimeoutSeconds"/>
/// (or when no enemy has been seen for several seconds and chests are visible), we move on.</para>
///
/// <para>Optional tower placement is restricted to visible foundations inside the pump bubble.
/// Build and upgrade clicks use the live-validated radial-menu contract and require entity/tier
/// postconditions. Map-device automation and the fast-forward button are also implemented.</para>
/// </summary>
public sealed class BlightMode : IBotMode
{
    private readonly Func<GameSnapshot?> _getSnapshot;
    private readonly Func<LivePlayer?>   _getLive;
    private readonly Func<EntityCache?>  _getEntities;
    private readonly SettingsStore _settings;

    private readonly MovementSystem _movement;
    private readonly CombatSystem   _combat = new();
    private readonly FlaskSystem    _flasks = new();
    private readonly InteractSystem _interact = new();
    private readonly SkillBook      _skills = new();
    private readonly MaintainSelfBuffs _combatBuffs;
    private readonly MaintainTargetedMark _combatMark;
    private readonly LootClosestVisible _loot;
    private readonly ExplorationSystem _locateExploration = new();
    private readonly ExplorationSystem _cleanupExploration = new();
    private readonly ExploreFrontier _locatePump;
    private readonly ExploreFrontier _cleanupExplore;
    private readonly EnterAreaTransition _resumePortal;
    private readonly BlightTowerController _towerController;
    private readonly FollowPath _towerApproachWalk;
    private readonly FollowPath _chestAnchorWalk;
    private readonly FollowPath _groundLootWalk;
    private readonly IBehavior _root;

    /// <summary>Pump grid position is captured the first tick we see one. Survives the pump
    /// falling out of the entity list after activation — the defend phase needs an anchor.</summary>
    private Vector2i? _pumpAnchor;

    /// <summary>Set the first tick we see the pump as activated (gone from MechanicsView). Used
    /// to gate the timeout and to know we're in defend phase, not start phase.</summary>
    private TimeSpan _encounterStartedAt = TimeSpan.MinValue;

    /// <summary>Wall-clock of the last tick we observed a hostile in attack range. Powers the
    /// "no enemies for a while → encounter is over" exit heuristic.</summary>
    private TimeSpan _lastEnemyAt = TimeSpan.MinValue;

    /// <summary>
    /// Cached pump entity ID — captured the first tick we see one. Survives the pump falling
    /// out of the available set after activation; the defend phase reads its StateMachine
    /// for success/fail/activated each tick.
    /// </summary>
    private uint _pumpEntityId;
    private int _pumpObservationTick;
    private LongObservation _pumpActivated = LongObservation.Unknown(
        "BlightPump.Activated", 0, ObservationReadStatus.NeverRead);
    private LongObservation _pumpReadyToStart = LongObservation.Unknown(
        "BlightPump.ReadyToStart", 0, ObservationReadStatus.NeverRead);
    private LongObservation _pumpSuccess = LongObservation.Unknown(
        "BlightPump.Success", 0, ObservationReadStatus.NeverRead);
    private LongObservation _pumpFail = LongObservation.Unknown(
        "BlightPump.Fail", 0, ObservationReadStatus.NeverRead);
    private bool _terminalResolvedLatched;
    private Vector2i? _exitPortalAnchor;

    /// <summary>
    /// Currently-targeted chest. Locked once we pick it; we don't re-pick a different chest
    /// until this one is opened or vanishes from the cache. Without this lock, the chest
    /// picker returns "closest unopened" every tick, the player walks closer to one chest
    /// then another flips to closest, and InteractWorldEntity sees a fresh target ID and
    /// resets its attempt counter — looks like the bot is bouncing between chests.
    /// </summary>
    private uint _lockedChestId;
    private TimeSpan _lockedChestAt = TimeSpan.MinValue;
    private readonly HashSet<uint> _skippedChestIds = new();
    private int _chestRetryPass;
    private const double ChestInteractionTimeoutSeconds = 8.0;
    private TimeSpan _chestLootDrainStartedAt = TimeSpan.MinValue;
    private TimeSpan _chestLootLastSeenAt = TimeSpan.MinValue;
    private Vector2i? _lastOpenedChestAnchor;
    private const double ChestLootQuietSeconds = 1.0;
    private const double ChestLootMaxDrainSeconds = 30.0;
    private const int ChestLootClusterRadiusGrid = 100;

    /// <summary>Hideout-to-map flow controller. Active only while <c>BlightAutoOpenMapDevice</c>
    /// is on AND the player is in a hideout. Drives the bot through map-device click →
    /// stage map → activate → portal entry, then stands aside as the in-map blight tree
    /// takes over after the area transition.</summary>
    private readonly MapDeviceSystem _mapDevice;
    private readonly StashDepositSystem _hideoutDeposit;
    private readonly StashTabSwitcher _supplyTabSwitcher;

    private enum HideoutPhase { Deposit, Supply, CloseStash, Ready, Stopped }
    private HideoutPhase _hideoutPhase = HideoutPhase.Deposit;
    private TimeSpan _supplyPanelObservedAt = TimeSpan.MinValue;
    private TimeSpan _supplyMissingSince = TimeSpan.MinValue;
    private TimeSpan _lastSupplyActionAt = TimeSpan.MinValue;
    private int _supplyClickAttempts;
    private bool _supplyWithdrawPending;
    private int _hideoutItemsDepositedTotal;
    private int _mapsWithdrawnTotal;
    private int _mapsCompletedTotal;
    private uint _successfulAreaAwaitingHideout;
    private readonly HashSet<uint> _countedSuccessfulAreas = new();
    private string _hideoutStatus = "deposit pending";
    private const int VkEscape = 0x1B;
    private const int VkLeftControl = 0xA2;
    private const int MaxSupplyClicks = 4;

    /// <summary>Wall-clock the device flow last failed. Used to throttle automatic restarts
    /// — without this, a transient failure (mid-loading-screen entity scan, brief UI lag)
    /// would re-Start the flow every render frame and spam clicks.</summary>
    private TimeSpan _deviceFlowLastFailedAt = TimeSpan.MinValue;
    private TimeSpan _lastGateLogAt = TimeSpan.MinValue;

    /// <summary>When the post-encounter sweep began. <c>MinValue</c> = sweep not yet started
    /// for this encounter. Once defend ends, the bot enters sweep until the configured
    /// duration elapses or no enemies are seen for several seconds.</summary>
    private TimeSpan _sweepStartedAt = TimeSpan.MinValue;
    /// <summary>Wall-clock of the last tick we saw a hostile within attack range during
    /// sweep. Used to early-exit sweep when the area is genuinely cleared.</summary>
    private TimeSpan _sweepLastEnemyAt = TimeSpan.MinValue;
    private uint _sweepTargetId;
    private Vector2i? _sweepGoal;
    private string _sweepGoalReason = "none";
    private int _cleanupRevealed;
    private int _cleanupTotal;
    private TimeSpan _terminalConfirmStartedAt = TimeSpan.MinValue;
    private int _sweepPass;

    /// <summary>Wall-clock of the last tick any hostile mob in the network bubble was
    /// observed with <c>IsMoving=true</c>. Updated every tick the bot sees movement; held
    /// constant when everything's dead or stuck. Drives the "wait for things to stop
    /// moving for N seconds before sweeping" gate after the encounter timer hits 0:00.
    /// Initialized to the tick we first see a hostile so the threshold doesn't false-fire
    /// the moment the bot enters a map.</summary>
    private TimeSpan _lastHostileMovementAt = TimeSpan.MinValue;

    /// <summary>Wall-clock the encounter timer was first observed at 0:00. <c>MinValue</c>
    /// while the timer is still running. Anchors the post-timer settle delay separately from
    /// the quiet-window gate so both can be configured independently.</summary>
    private TimeSpan _timerDoneAt = TimeSpan.MinValue;
    private bool _sawActiveCountdown;

    /// <summary>Wall-clock the bot last tried to click the skip-button. Used to throttle the
    /// click so we don't spam SendInput while the button is animating in.</summary>
    private TimeSpan _lastSkipClickAt = TimeSpan.MinValue;

    /// <summary>Set once the skip button has been clicked AND the bot has observed the button
    /// disappear (or enough time has elapsed). Prevents re-clicks during the rest of the
    /// encounter — even if the panel briefly re-shows for animations.</summary>
    private bool _skipButtonClicked;
    private string _defendGoalReason = "hold-pump";
    private string _phase = "Idle";
    private string _lastReportedPhase = string.Empty;
    private string? _countdown;
    private bool _skipVisible;
    private int _eligibleHostiles;
    private int _laneMarkersSeen;
    private int _unopenedChests;

    public string Name => "Blight";
    public IBehavior Root => _root;
    public string LastDecision { get; private set; } = "init";
    public IReadOnlyList<string> HudLines => BuildHudLines();
    public object PumpTelemetry => new
    {
        entityId = _pumpEntityId,
        anchor = _pumpAnchor is { } p ? new { x = p.X, y = p.Y } : null,
        activated = _pumpActivated,
        readyToStart = _pumpReadyToStart,
        success = _pumpSuccess,
        fail = _pumpFail,
        terminalResolvedLatched = _terminalResolvedLatched,
        portalAnchor = _exitPortalAnchor is { } portal ? new { x = portal.X, y = portal.Y } : null,
        phase = _phase,
        countdown = _countdown,
        timerDone = _timerDoneAt != TimeSpan.MinValue,
        timerUiDone = BlightTimerView.IsTimerDone(_countdown),
        sawActiveCountdown = _sawActiveCountdown,
        skipVisible = _skipVisible,
        encounterElapsedSeconds = _encounterStartedAt == TimeSpan.MinValue
            ? (double?)null
            : (BotMonotonicClock.Now - _encounterStartedAt).TotalSeconds,
        hostileQuietSeconds = _lastHostileMovementAt == TimeSpan.MinValue
            ? (double?)null
            : (BotMonotonicClock.Now - _lastHostileMovementAt).TotalSeconds,
        sweepElapsedSeconds = _sweepStartedAt == TimeSpan.MinValue
            ? (double?)null
            : (BotMonotonicClock.Now - _sweepStartedAt).TotalSeconds,
        eligibleHostiles = _eligibleHostiles,
        laneMarkersSeen = _laneMarkersSeen,
        unopenedChests = _unopenedChests,
        skippedChests = _skippedChestIds.Count,
        chestRetryPass = _chestRetryPass,
        towerBusy = _towerController.IsBusy,
        towerStatus = _towerController.Status,
        towerCurrency = _towerController.LastCurrency,
        towerNavigationGoal = _towerController.NavigationGoal is { } towerGoal
            ? new { x = towerGoal.X, y = towerGoal.Y }
            : null,
        cleanupTargetId = _sweepTargetId,
        cleanupGoal = _sweepGoal is { } goal ? new { x = goal.X, y = goal.Y } : null,
        cleanupGoalReason = _sweepGoalReason,
        cleanupExplorationExhausted = _cleanupExploration.IsExhausted,
        cleanupRevealed = _cleanupRevealed,
        cleanupTotal = _cleanupTotal,
        sweepPass = _sweepPass,
        terminalConfirmSeconds = _terminalConfirmStartedAt == TimeSpan.MinValue
            ? (double?)null
            : (BotMonotonicClock.Now - _terminalConfirmStartedAt).TotalSeconds,
        defendGoalReason = _defendGoalReason,
        lockedChestId = _lockedChestId,
        hideoutPhase = _hideoutPhase.ToString(),
        hideoutStatus = _hideoutStatus,
        hideoutItemsDepositedTotal = _hideoutItemsDepositedTotal,
        mapsWithdrawnTotal = _mapsWithdrawnTotal,
        mapsCompletedTotal = _mapsCompletedTotal,
        devicePhase = _mapDevice.CurrentPhase.ToString(),
        deviceStatus = _mapDevice.Status,
    };

    public BlightMode(SettingsStore settings, Func<GameSnapshot?> getSnapshot, Func<LivePlayer?> getLive, Func<EntityCache?> getEntities)
    {
        _settings    = settings;
        _getSnapshot = getSnapshot;
        _getLive     = getLive;
        _getEntities = getEntities;
        _movement    = new MovementSystem(settings);
        _combatBuffs = new MaintainSelfBuffs("Blight combat buffs", _combat, _skills);
        _combatMark = new MaintainTargetedMark("Blight targeted mark", _combat, _skills);
        _loot        = new LootClosestVisible("loot closest", _interact, getSnapshot);
        _loot.PickupConfirmed += _ =>
        {
            if (_chestLootDrainStartedAt != TimeSpan.MinValue)
                _chestLootLastSeenAt = BotMonotonicClock.Now;
        };
        _mapDevice   = new MapDeviceSystem(_movement, _skills, getSnapshot, getLive, getEntities);
        _hideoutDeposit = new StashDepositSystem(
            _movement, _skills, getSnapshot,
            (inventory, item) => BlightInventoryPolicy.ShouldRetainForNextRun(
                inventory.Items, item));
        _supplyTabSwitcher = new StashTabSwitcher(getSnapshot);
        _towerController = new BlightTowerController(getSnapshot, getEntities);
        _towerApproachWalk = new FollowPath("approach Blight tower", _movement,
            _ => _towerController.NavigationGoal,
            _skills,
            goalArrivalRadiusProvider: ctx => ctx.Settings.BlightTowerApproachDistance,
            allowGapCrossing: false);
        _locatePump  = new ExploreFrontier("locate blight pump", _locateExploration, _movement, _skills);
        _cleanupExplore = new ExploreFrontier(
            "blight cleanup exploration", _cleanupExploration, _movement, _skills);
        _resumePortal = new EnterAreaTransition(
            "resume existing Blight portal", _interact, _movement, _skills, getSnapshot,
            entity => entity.Kind is EntityListReader.EntityKind.TownPortal
                or EntityListReader.EntityKind.Portal);

        // Pump click — verified by the pump's StateMachine flipping `activated > 0` on the
        // next tick (handled by PumpNeedsClick going false). InteractWorldEntity walks to the
        // pump then clicks; we only switch here once ReadyToStart > 0 so we don't burn click
        // attempts on a pre-spawn pump that won't accept input.
        var pumpClick = new InteractWorldEntity("pump", _interact, _movement, _skills,
            ctx => PickPump(ctx),
            (_, _) => false);   // success is signalled by PumpNeedsClick going false (activated>0)

        // Pump approach — fires BEFORE ReadyToStart flips. The pump's StateMachine doesn't
        // populate `ready_to_start` until the player is close enough for the encounter UI to
        // arm, so the bot must walk to the pump even when the click predicate is still false.
        // The entity itself is visible from the moment we enter the map (BlightPump path is
        // streamed in with the area), so we have a valid grid anchor immediately.
        var pumpApproach = new FollowPath("approach pump", _movement,
            ctx => PumpNeedsApproach(ctx) ? _pumpAnchor : null,
            _skills,
            goalArrivalRadius: 12f);

        // Chest click — generic Chest entity with "Blight" in the path. IsOpened on the chest
        // component flips post-click; we use IsTargetable as the universal proxy (consistent
        // with MechanicsView) so we don't need to read Chest.IsOpened separately.
        var chestClick = new InteractWorldEntity("blight chest", _interact, _movement, _skills,
            ctx => PickClosestBlightChest(ctx),
            (_, e) => e.IsOpened,
            interactionRangeGrid: 25f,
            allowGapCrossing: false);

        // Blight reward labels remain globally enumerated after encounter completion, even
        // when their backing entity is outside the network bubble. Walk those durable label
        // anchors; the interaction behavior above takes over only after the entity is fresh.
        _chestAnchorWalk = new FollowPath("blight chest anchor", _movement,
            PickClosestBlightChestAnchor, _skills, goalArrivalRadius: 12f,
            allowGapCrossing: false);
        _groundLootWalk = new FollowPath("blight chest-drop anchor", _movement,
            PickClosestChestDropAnchor, _skills,
            goalArrivalRadiusProvider: ctx => MathF.Min(25f, ctx.Settings.LootRangeGrid),
            allowGapCrossing: false);

        var attackBranch = new If("attack in range", InAttackRange,
            new Cast("attack", _combat, ctx => PickAttackSlot(ctx.Settings),
                Aim.AtClosestEnemy(60f), _skills));

        // "Anchor at pump" — walks back to the pump anchor when out of defend radius. Inside
        // the radius this returns Failure so the parallel falls through to combat alone.
        var anchorWalk = new FollowPath("anchor", _movement,
            GetDefendGoal,
            _skills,
            goalArrivalRadius: 6f);

        // Post-encounter sweep — orbits/spirals out from the pump anchor, attacking any
        // mobs still around. Runs after defend phase ends, before chest looting. Picks a
        // walk goal at angle θ around the anchor at increasing radius; θ advances each tick
        // we're within the goal-arrival window. Combat runs in parallel via attackBranch.
        var sweepWalk = new FollowPath("sweep walk", _movement,
            GetSweepGoal,
            _skills,
            goalArrivalRadius: 6f);

        // A quiet/timed-out sweep is not proof that Blight completed. Return to the cached
        // pump so its state machine is inside the network bubble, wait for success/fail to
        // update, and start another lane pass if it remains active.
        var confirmAtPump = new FollowPath("confirm at pump", _movement,
            _ => _pumpAnchor,
            _skills,
            goalArrivalRadius: 20f);

        _root = new Selector("blight",
            new If("low HP", LowHp,
                new Sequence("retreat",
                    new StopMoving("halt", _movement),
                    new Behaviors.Action("wait HP", _ => BehaviorStatus.Success))),
            // A fresh map can attach before the pump enters the initial network bubble.
            // Physically explore until the entity is observed; absence is never evidence
            // that the encounter is complete and must never fall through to the exit portal.
            new If("locate pump",
                _ => _pumpEntityId == 0,
                new Behaviors.Action("explore for pump", ctx =>
                {
                    _locatePump.Tick(ctx);
                    return BehaviorStatus.Running;
                })),
            // Walk to the pump while it's not yet ready_to_start. Walking close is what
            // arms the encounter UI server-side, so this branch must fire BEFORE the click
            // branch can become viable. Goes silent once ReadyToStart flips to 1.
            new If("approach pump",
                ctx => PumpNeedsApproach(ctx),
                pumpApproach),
            // Pump click — only fires when the pump's StateMachine reports
            // ready_to_start=1 AND activated=0. Once the player clicks (activated>0) this
            // branch goes silent because the encounter is live; the defend branch picks up.
            new If("pump available",
                ctx => PumpNeedsClick(ctx),
                pumpClick),
            // Fail closed while the pump exists but its ready/activation contract is not
            // actionable yet. This blocks a transient StateMachine read loss from reaching
            // the terminal exit branch before the encounter has even started.
            new If("wait for pump contract",
                _ => _encounterStartedAt == TimeSpan.MinValue,
                new Behaviors.Action("hold before encounter", _ => BehaviorStatus.Running)),
            // Defend phase — anchor + attack while encounter is live and hasn't timed out.
            // We can't use Parallel here: this codebase's Parallel returns Failure if ANY
            // child fails, and during a normal idle moment in defend phase BOTH children
            // legitimately return Failure (anchor walk → no goal because we're already in
            // radius; attack → no enemy in attack range right this tick). The Selector
            // would then fall through to EnterAreaTransition and portal home. Wrap in an
            // Action that ticks both children for side-effects and unconditionally returns
            // Running so the parent Selector stays parked on us until InDefendPhase flips.
            new If("defend pump", InDefendPhase,
                new Behaviors.Action("defend (anchor+attack)", ctx =>
                {
                    // Skip-button click — fast-forwards the pre-wave wait period. The button
                    // only appears between pump activation and the first wave arriving. One
                    // click is enough; further ticks are no-ops via _skipButtonClicked.
                    TryClickSkipButton(ctx);

                    // Pump-local tower construction has priority only during a safe gap. The
                    // controller never walks down lanes and immediately yields to combat when
                    // a hostile is within 25 grid units.
                    var inRange = InAttackRange(ctx);
                    var defendGoal = GetDefendGoal(ctx);
                    var hasVisibleThreat = HasEligibleHostile(ctx);

                    // Mutually exclusive: finish an enemy already in attack range XOR walk
                    // back to the pump. Running
                    // both each tick was causing drive-by drift — anchor walk holds the
                    // walk key with cursor on the pump path, then attack redirects cursor
                    // to an enemy while the walk key is still held, so the player walks
                    // toward the enemy. Net effect was the bot creeping away from the
                    // pump while the timer was still running. Hold-position behavior
                    // requires releasing movement before tapping skills.
                    if (defendGoal is not null)
                    {
                        _towerController.PreemptForDefense();
                        _towerApproachWalk.Reset();
                        anchorWalk.Tick(ctx);
                    }
                    else if (inRange)
                    {
                        _towerController.PreemptForDefense();
                        _towerApproachWalk.Reset();
                        _movement.Release();
                        MaintainCombatUtilities(ctx);
                        attackBranch.Tick(ctx);
                    }
                    else if (hasVisibleThreat)
                    {
                        _towerController.PreemptForDefense();
                        _towerApproachWalk.Reset();
                        _movement.Release();
                    }
                    else if (_timerDoneAt == TimeSpan.MinValue
                        && _pumpAnchor is { } pump
                        && _towerController.Tick(ctx, pump))
                    {
                        if (_towerController.NavigationGoal is not null)
                            _towerApproachWalk.Tick(ctx);
                        else
                        {
                            _towerApproachWalk.Reset();
                            _movement.Release();
                        }
                    }
                    else
                    {
                        _towerApproachWalk.Reset();
                        // No enemy in attack range AND we're standing on the pump anchor.
                        // Use this idle window to grab any drops already within loot range
                        // — items dropped by killed mobs that we'd otherwise miss until the
                        // post-encounter chest sweep. Only fires when there IS loot in range
                        // so we don't release movement gratuitously.
                        if (HasLootInRange(ctx))
                        {
                            _movement.Release();
                            _loot.Tick(ctx);
                        }
                    }
                    return BehaviorStatus.Running;
                })),
            // Post-encounter sweep — fires once after defend ends. Orbits the pump killing
            // stragglers + waiting for chests to become clickable. The user reported that
            // chest interaction is sometimes blocked until the encounter is FULLY done
            // (animation, server-side reconciliation), so this also serves as a settle
            // window. Exits early when no enemies seen for ~5s.
            new If("post-encounter sweep", InSweepPhase,
                new Behaviors.Action("sweep (orbit+attack)", ctx =>
                {
                    if (_sweepStartedAt == TimeSpan.MinValue)
                    {
                        _sweepStartedAt   = BotMonotonicClock.Now;
                        _sweepLastEnemyAt = BotMonotonicClock.Now;
                        BubblesBot.Bot.Diagnostics.EventLog.Log("Blight",
                            "post-encounter sweep started");
                    }
                    var inRange = InAttackRange(ctx);
                    var hasHostile = HasEligibleHostile(ctx);
                    if (hasHostile) _sweepLastEnemyAt = BotMonotonicClock.Now;

                    // Ambient loot during sweep. Prioritized over orbital movement when
                    // no enemy is in attack range AND something's already in loot range —
                    // standing still to pick it up is cheaper than completing the spiral
                    // and circling back. Items dropped by mobs the bot kills mid-sweep get
                    // grabbed without waiting for the post-sweep chest phase.
                    if (!inRange && HasLootInRange(ctx))
                    {
                        _movement.Release();
                        _loot.Tick(ctx);
                    }
                    else if (hasHostile)
                    {
                        // Do not redirect the cursor with an attack while E is held. Walk to
                        // the safe standoff first, release movement, then let the build's
                        // configured combat skills engage from nearby.
                        var approach = sweepWalk.Tick(ctx);
                        if (approach == BehaviorStatus.Success)
                        {
                            _movement.Release();
                            MaintainCombatUtilities(ctx);
                            attackBranch.Tick(ctx);
                        }
                    }
                    else
                    {
                        _sweepTargetId = 0;
                        _cleanupExplore.Tick(ctx);
                        _sweepGoal = _cleanupExplore.Follow.Goal;
                        _sweepGoalReason = $"explore:{_cleanupExploration.LastFrontierReason}";
                        (_cleanupRevealed, _cleanupTotal) = _cleanupExploration.Progress(ctx);
                    }
                    return BehaviorStatus.Running;
                })),
            new If("confirm encounter result", NeedsTerminalConfirmation,
                new Behaviors.Action("return to pump and confirm", ctx =>
                {
                    var approach = confirmAtPump.Tick(ctx);
                    if (approach != BehaviorStatus.Success)
                    {
                        _terminalConfirmStartedAt = TimeSpan.MinValue;
                        return BehaviorStatus.Running;
                    }

                    if (_terminalConfirmStartedAt == TimeSpan.MinValue)
                    {
                        _terminalConfirmStartedAt = BotMonotonicClock.Now;
                        Diagnostics.EventLog.Emit(
                            "blight", "blight.terminal-confirm-started",
                            Diagnostics.EventSeverity.Info,
                            "returned to pump network bubble; awaiting terminal state",
                            new Dictionary<string, object?> { ["sweepPass"] = _sweepPass });
                    }

                    // Pump state is refreshed at the start of every mode tick. Give the
                    // server several coherent reads before declaring another physical pass.
                    if ((BotMonotonicClock.Now - _terminalConfirmStartedAt).TotalSeconds < 3.0)
                        return BehaviorStatus.Running;

                    _sweepPass++;
                    _sweepStartedAt = TimeSpan.MinValue;
                    _sweepLastEnemyAt = BotMonotonicClock.Now;
                    _terminalConfirmStartedAt = TimeSpan.MinValue;
                    _sweepTargetId = 0;
                    _sweepGoal = null;
                    _sweepGoalReason = "resweep-reset";
                    _cleanupExploration.Reset();
                    _cleanupExplore.Reset();
                    sweepWalk.Reset();
                    Diagnostics.EventLog.Emit(
                        "blight", "blight.terminal-unresolved-resweep",
                        Diagnostics.EventSeverity.Warning,
                        $"pump remained active after sweep; starting lane pass {_sweepPass}",
                        new Dictionary<string, object?> { ["sweepPass"] = _sweepPass });
                    return BehaviorStatus.Running;
                })),
            // Loot ground items in range (blight drops + chest contents).
            new If("loot in range", HasLootInRange,
                new Sequence("loot",
                    new StopMoving("halt loot", _movement),
                    _loot)),
            // Once a chest opens, finish its nearby visible/allowed drop cluster before
            // selecting another chest. This prevents chest movement from pulling one item
            // into range, then away from the rest, then back again.
            new If("drain chest drops", IsChestLootDrainActive,
                new Behaviors.Action("seek chest drops", ctx =>
                {
                    _groundLootWalk.Tick(ctx);
                    return BehaviorStatus.Running;
                })),
            // Walk to the next unopened chest. We deliberately gate ONLY on the user setting,
            // not on "chest available right now" — if we re-evaluated the picker in the If
            // predicate, transient cache evictions (chest briefly drops out of network bubble)
            // would flip the predicate false for one tick, the If would Reset() the child, and
            // InteractWorldEntity would zero its _attempts counter, causing the "attempt 1/4
            // forever" thrash. The InteractWorldEntity itself returns Failure when its
            // selector returns null, which lets the parent Selector fall through naturally.
            new If("loot chests on",
                ctx => ctx.Settings.BlightLootChests,
                chestClick),
            new If("seek remaining blight chest labels",
                ctx => ctx.Settings.BlightLootChests,
                _chestAnchorWalk),
            // Nothing left to do here — exit via portal.
            new If("terminal result confirmed", IsEncounterResolved,
                new EnterAreaTransition("exit", _interact, _movement, _skills, getSnapshot,
                    IsMapReturnPortal,
                    // Reward paths can extend well beyond the portal's network bubble. Walk
                    // back to its last fresh position, then let EnterAreaTransition require a
                    // fresh label before authorizing the click. Pump is a conservative recovery
                    // anchor for a process restart that lost the portal cache mid-map.
                    fallbackGrid: _ => _exitPortalAnchor ?? _pumpAnchor,
                    allowGapCrossing: false)),
            new Behaviors.Action("hold unresolved blight", ctx =>
            {
                _movement.Halt(new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live));
                return BehaviorStatus.Running;
            }));
    }

    public void Reset()
    {
        _movement.Release();
        _combat.StopAllChannels();
        _flasks.Reset();
        _interact.Cancel();
        _skills.Reset();
        _combatBuffs.Reset();
        _combatMark.Reset();
        _loot.Reset();
        _locateExploration.Reset();
        _locatePump.Reset();
        _resumePortal.Reset();
        _root.Reset();
        _pumpAnchor = null;
        _pumpEntityId = 0;
        _pumpObservationTick = 0;
        _pumpActivated = LongObservation.Unknown("BlightPump.Activated", 0, ObservationReadStatus.NeverRead);
        _pumpReadyToStart = LongObservation.Unknown("BlightPump.ReadyToStart", 0, ObservationReadStatus.NeverRead);
        _pumpSuccess = LongObservation.Unknown("BlightPump.Success", 0, ObservationReadStatus.NeverRead);
        _pumpFail = LongObservation.Unknown("BlightPump.Fail", 0, ObservationReadStatus.NeverRead);
        _terminalResolvedLatched = false;
        _exitPortalAnchor = null;
        _lockedChestId = 0;
        _lockedChestAt = TimeSpan.MinValue;
        _skippedChestIds.Clear();
        _chestRetryPass = 0;
        _chestAnchorWalk.Reset();
        _groundLootWalk.Reset();
        _towerController.Reset();
        _towerApproachWalk.Reset();
        _chestLootDrainStartedAt = TimeSpan.MinValue;
        _chestLootLastSeenAt = TimeSpan.MinValue;
        _lastOpenedChestAnchor = null;
        _encounterStartedAt = TimeSpan.MinValue;
        _lastEnemyAt = TimeSpan.MinValue;
        _mapDevice.Cancel();
        _hideoutDeposit.Cancel();
        _supplyTabSwitcher.Reset();
        _hideoutPhase = HideoutPhase.Deposit;
        _supplyPanelObservedAt = TimeSpan.MinValue;
        _supplyMissingSince = TimeSpan.MinValue;
        _lastSupplyActionAt = TimeSpan.MinValue;
        _supplyClickAttempts = 0;
        _supplyWithdrawPending = false;
        _hideoutStatus = "deposit pending";
        _deviceFlowLastFailedAt = TimeSpan.MinValue;
        _sweepStartedAt   = TimeSpan.MinValue;
        _sweepLastEnemyAt = TimeSpan.MinValue;
        _sweepTargetId = 0;
        _sweepGoal = null;
        _sweepGoalReason = "none";
        _cleanupRevealed = 0;
        _cleanupTotal = 0;
        _cleanupExploration.Reset();
        _cleanupExplore.Reset();
        _terminalConfirmStartedAt = TimeSpan.MinValue;
        _sweepPass = 0;
        _lastHostileMovementAt = TimeSpan.MinValue;
        _timerDoneAt = TimeSpan.MinValue;
        _sawActiveCountdown = false;
        _lastSkipClickAt = TimeSpan.MinValue;
        _defendGoalReason = "hold-pump";
        _phase = "Idle";
        _lastReportedPhase = string.Empty;
        _countdown = null;
        _skipVisible = false;
        _eligibleHostiles = 0;
        _laneMarkersSeen = 0;
        _unopenedChests = 0;
        _skipButtonClicked = false;
        LastDecision = "reset";
    }

    public void Tick(GameSnapshot snapshot, IInputRouter input)
    {
        if (snapshot.Player is { } pv) _skills.SetActorContext(pv.ActorComponentAddress);
        if (_skills.CooldownReader is null) _skills.CooldownReader = new SkillCooldownReader(snapshot.Reader);

        var ctx = new BehaviorContext(snapshot, input, _settings.Current, _getLive(), _getEntities());

        // Hideout gate — hard-blocks the in-map tree as long as we're standing in a
        // hideout/town. The previous version only suspended the tree while
        // _mapDevice.IsBusy, which let the in-map EnterAreaTransition branch run during
        // any gap (flow not yet started, flow just failed, between phases) and walk into
        // stale portals from the previous map. Now: while in hideout AND auto-open is on,
        // ONLY the device flow runs. The in-map tree is unreachable until the area changes.
        //
        // Loss-of-confidence escape hatch: if the flow fails, we wait a backoff window
        // before re-Starting, but we still don't fall through to the main tree. Worst case
        // the bot sits idle in hideout — much safer than running the previous map again.
        // Hideout-to-map flow is core to the mode — not a setting. While in a hideout (map
        // device entity present), only the device flow runs. The in-map tree is locked off
        // to prevent stale-portal walkthroughs.
        var inHideout = IsLikelyInHideout(ctx);

        if (!inHideout && ctx.Settings.BlightResumeExistingPortalOnce)
            _settings.Mutate(settings => settings.BlightResumeExistingPortalOnce = false);

        // Log the gate decision once per second so the dashboard reflects what's happening
        // even when the device flow isn't starting.
        if (_lastGateLogAt == TimeSpan.MinValue
            || (BotMonotonicClock.Now - _lastGateLogAt).TotalSeconds > 1.0)
        {
            BubblesBot.Bot.Diagnostics.EventLog.Log("Blight",
                $"hideout-gate: inHideout={inHideout} mapDevice.phase={_mapDevice.CurrentPhase}");
            _lastGateLogAt = BotMonotonicClock.Now;
        }

        if (inHideout)
        {
            if (_successfulAreaAwaitingHideout != 0
                && _countedSuccessfulAreas.Add(_successfulAreaAwaitingHideout))
            {
                _mapsCompletedTotal++;
                Diagnostics.EventLog.Emit(
                    "blight", "blight.map-cycle-completed", Diagnostics.EventSeverity.Info,
                    $"successful Blight map {_mapsCompletedTotal} returned to hideout",
                    new Dictionary<string, object?>
                    {
                        ["mapsCompletedTotal"] = _mapsCompletedTotal,
                        ["areaHash"] = $"0x{_successfulAreaAwaitingHideout:X8}",
                    });
            }
            _successfulAreaAwaitingHideout = 0;
            UpdateLiveTelemetry(ctx, inHideout: true);
            const int RestartBackoffSeconds = 5;

            if (ctx.Settings.BlightResumeExistingPortalOnce)
            {
                var recoveryStatus = _resumePortal.Tick(ctx);
                LastDecision = $"Recovery/ExistingPortal: {recoveryStatus}";
                return;
            }

            // If activation succeeded but every direct portal click timed out, the map is
            // already open. Recover through the durable ground-label transition behavior;
            // never restart the device flow here, because doing so would consume another
            // carried map while abandoning the valid portals we just created.
            if (_mapDevice.CurrentPhase == MapDeviceSystem.Phase.Failed
                && _mapDevice.Status.Contains("EnterPortal", StringComparison.OrdinalIgnoreCase))
            {
                var recoveryStatus = _resumePortal.Tick(ctx);
                LastDecision = $"Recovery/ActivatedMapPortal: {recoveryStatus}; {_mapDevice.Status}";
                _flasks.Tick(ctx);
                return;
            }

            if (!TickHideoutPreparation(ctx))
            {
                LastDecision = $"Hideout/{_hideoutPhase}: {_hideoutStatus}";
                UpdateLiveTelemetry(ctx, inHideout: true);
                _flasks.Tick(ctx);
                return;
            }

            if (!_mapDevice.IsBusy)
            {
                if (_mapDevice.CurrentPhase == MapDeviceSystem.Phase.Failed)
                {
                    // Wait the backoff before retrying.
                    var since = (BotMonotonicClock.Now - _deviceFlowLastFailedAt).TotalSeconds;
                    if (since < RestartBackoffSeconds)
                    {
                        LastDecision = $"mapDevice failed — backoff {RestartBackoffSeconds - since:F0}s";
                        _flasks.Tick(ctx);
                        return;
                    }
                    BubblesBot.Bot.Diagnostics.EventLog.Log("Blight",
                        $"retrying map device flow after {since:F0}s backoff");
                }
                // Blighted and Blight-ravaged maps are boss-style map payloads: right-click
                // the single preflighted carried MapKey to select its atlas node and stage it.
                // MapDeviceSystem fails closed if inventory contains multiple map candidates.
                _mapDevice.Start(ctx.Entities, MapDeviceSystem.PayloadSource.InventoryMap);
            }

            var r = _mapDevice.Tick(ctx);
            if (r == MapDeviceSystem.Result.Failed)
            {
                _deviceFlowLastFailedAt = BotMonotonicClock.Now;
                BubblesBot.Bot.Diagnostics.EventLog.Log("Blight",
                    $"map device flow FAILED: {_mapDevice.Status} — will retry in {RestartBackoffSeconds}s");
            }
            LastDecision = $"mapDevice phase={_mapDevice.CurrentPhase} status={_mapDevice.Status}";
            UpdateLiveTelemetry(ctx, inHideout: true);
            _flasks.Tick(ctx);
            return;
        }

        // Capture / update pump anchor + encounter timestamp before the tree ticks. The
        // "available" pump comes from MechanicsView; once it's no longer available the
        // encounter is live and we lock in the anchor.
        UpdatePumpState(ctx);
        UpdateExitPortalAnchor(ctx);
        if (_pumpSuccess is { IsKnown: true, Value: > 0 })
            _successfulAreaAwaitingHideout = snapshot.AreaHash;

        // Anchor the post-timer settle window. The first tick we observe "0:00" stamps
        // _timerDoneAt; the defend/sweep predicates measure their delays off this anchor
        // so users can configure "wait N seconds after timer hits 0" independently of the
        // quiet-window for monster movement.
        var encounterIsActive = _encounterStartedAt != TimeSpan.MinValue
            && _pumpActivated is { IsKnown: true, Value: > 0 };
        if (encounterIsActive && !ctx.Snapshot.IsBlightTimerDone
            && !string.IsNullOrWhiteSpace(ctx.Snapshot.BlightCountdown))
            _sawActiveCountdown = true;

        if (encounterIsActive
            && _sawActiveCountdown
            && ctx.Snapshot.IsBlightTimerDone
            && _timerDoneAt == TimeSpan.MinValue)
        {
            _timerDoneAt = BotMonotonicClock.Now;
            // Timer completion is a hard tower-work cutoff. Defend intentionally remains
            // active during the settle/quiet windows, but only pump hold is valid now.
            _towerController.Reset();
            _towerApproachWalk.Reset();
            _movement.Release();
            BubblesBot.Bot.Diagnostics.EventLog.Log("Blight", "timer reached 0:00 — post-timer delay started");
        }

        if (InAttackRange(ctx)) _lastEnemyAt = BotMonotonicClock.Now;

        // Movement watchdog. Used to detect the "everything is dead or stuck" condition that
        // gates defend → sweep transition. We update the timestamp on the FIRST hostile we
        // see moving each tick; if we go through every entity and find none moving, the
        // timestamp stays where it was — and after enough seconds that gap is the signal.
        UpdateHostileMovementTracking(ctx);
        UpdateChestCompletion(ctx);
        UpdateLiveTelemetry(ctx, inHideout: false);

        _flasks.Tick(ctx);
        var status = _root.Tick(ctx);
        var sinceEnemy = _lastEnemyAt == TimeSpan.MinValue
            ? "never"
            : $"{(BotMonotonicClock.Now - _lastEnemyAt).TotalSeconds:F0}s";
        LastDecision = $"{PhaseDisplay(ctx)} | tree={status} | mobs={_eligibleHostiles} | sinceEnemy={sinceEnemy}";
    }

    /// <summary>
    /// Complete the deterministic between-run hideout preflight before the map device is
    /// allowed to consume anything: Dump all loot, switch to Supplies, withdraw exactly one
    /// positively identified Blight-ravaged map, then close the stash. Returns true only
    /// after that contract is satisfied.
    /// </summary>
    private bool TickHideoutPreparation(BehaviorContext ctx)
    {
        switch (_hideoutPhase)
        {
            case HideoutPhase.Deposit:
            {
                if (_hideoutDeposit.CurrentPhase == StashDepositSystem.Phase.Idle)
                {
                    var dumpTab = ctx.Settings.BlightDumpTabName.Trim();
                    if (dumpTab.Length == 0)
                    {
                        StopBlightLoop("Blight dump tab name is empty");
                        return false;
                    }
                    _hideoutDeposit.Start(dumpTab);
                }

                var result = _hideoutDeposit.Tick(ctx);
                _hideoutStatus = _hideoutDeposit.Status;
                if (result == StashDepositSystem.Result.Failed)
                {
                    StopBlightLoop($"loot deposit failed: {_hideoutDeposit.Status}");
                    return false;
                }
                if (result != StashDepositSystem.Result.Succeeded)
                    return false;

                _hideoutItemsDepositedTotal += _hideoutDeposit.Deposited;
                _hideoutPhase = HideoutPhase.Supply;
                _supplyPanelObservedAt = TimeSpan.MinValue;
                _supplyMissingSince = TimeSpan.MinValue;
                _supplyClickAttempts = 0;
                _supplyWithdrawPending = false;
                _supplyTabSwitcher.Reset();
                _hideoutStatus = $"dumped {_hideoutDeposit.Deposited} item(s); locating supply";
                Diagnostics.EventLog.Emit(
                    "blight", "blight.hideout-deposit-completed",
                    Diagnostics.EventSeverity.Info, _hideoutStatus,
                    new Dictionary<string, object?>
                    {
                        ["deposited"] = _hideoutDeposit.Deposited,
                        ["totalDeposited"] = _hideoutItemsDepositedTotal,
                        ["dumpTab"] = ctx.Settings.BlightDumpTabName,
                    });
                return false;
            }

            case HideoutPhase.Supply:
                return TickBlightSupply(ctx);

            case HideoutPhase.CloseStash:
                if (!ctx.Snapshot.IsStashOpen)
                {
                    _hideoutPhase = HideoutPhase.Ready;
                    _hideoutStatus = "one verified map carried; map device ready";
                    return true;
                }
                ctx.Input.VerifiedTapKey(
                    VkEscape, ClickIntent.InteractUi, "close Blight supply stash",
                    expectResolved: () => !(_getSnapshot()?.IsStashOpen ?? true),
                    timeoutMs: 2000);
                _hideoutStatus = "closing supply stash";
                return false;

            case HideoutPhase.Ready:
                return true;

            default:
                return false;
        }
    }

    private bool TickBlightSupply(BehaviorContext ctx)
    {
        // StashDepositSystem deliberately leaves the stash open. If an external action closed
        // it, restart the safe deposit/open sequence instead of clicking blind UI coordinates.
        if (!ctx.Snapshot.IsStashOpen)
        {
            _hideoutDeposit.Cancel();
            _hideoutPhase = HideoutPhase.Deposit;
            _hideoutStatus = "stash closed during supply step; reopening safely";
            return false;
        }

        var carried = CountCarriedBlightRavagedMaps(ctx.Snapshot.Inventory);
        if (carried > 0)
        {
            // A Blight-ravaged map is an unstackable item. More than one means our exactly-one
            // preflight invariant was violated, so do not hand an ambiguous inventory to the
            // map device.
            if (carried != 1)
            {
                StopBlightLoop($"expected exactly one carried Blight-ravaged map, found {carried}");
                return false;
            }
            if (_supplyWithdrawPending)
            {
                _supplyWithdrawPending = false;
                _mapsWithdrawnTotal++;
                Diagnostics.EventLog.Emit(
                    "blight", "blight.supply-withdraw-confirmed",
                    Diagnostics.EventSeverity.Info,
                    "one Blight-ravaged map appeared in player inventory",
                    new Dictionary<string, object?>
                    {
                        ["withdrawalsTotal"] = _mapsWithdrawnTotal,
                    });
            }
            _supplyMissingSince = TimeSpan.MinValue;
            _hideoutPhase = HideoutPhase.CloseStash;
            _hideoutStatus = "verified one carried Blight-ravaged map";
            return false;
        }

        var supplyTabName = ctx.Settings.BlightSupplyTabName.Trim();
        if (supplyTabName.Length == 0)
        {
            StopBlightLoop("Blight supply tab name is empty");
            return false;
        }

        var targetTab = ctx.Snapshot.StashTabs.Find(
            supplyTabName, requireGeneralPurpose: false);
        if (targetTab is null)
        {
            StopBlightLoop($"supply stash tab '{supplyTabName}' not found");
            return false;
        }

        var stash = ctx.Snapshot.StashInventory;
        if (ctx.Snapshot.StashTabs.FindSelected(
                supplyTabName, requireGeneralPurpose: false, stash.VisibleTabIndex) is null)
        {
            if (!_supplyTabSwitcher.IsStarted
                || !_supplyTabSwitcher.TargetName.Equals(
                    supplyTabName, StringComparison.OrdinalIgnoreCase))
                _supplyTabSwitcher.Start(supplyTabName, requireGeneralPurpose: false);
            var switchResult = _supplyTabSwitcher.Tick(ctx);
            _hideoutStatus = _supplyTabSwitcher.Status;
            if (switchResult == StashTabSwitcher.Result.Failed)
                StopBlightLoop($"supply-tab switch failed: {_supplyTabSwitcher.Status}");
            return false;
        }

        if (_supplyTabSwitcher.IsStarted)
        {
            _supplyTabSwitcher.Reset();
            _supplyPanelObservedAt = BotMonotonicClock.Now;
            _hideoutStatus = $"on '{supplyTabName}'; settling item layout";
            return false;
        }
        if (_supplyPanelObservedAt == TimeSpan.MinValue)
        {
            _supplyPanelObservedAt = BotMonotonicClock.Now;
            _hideoutStatus = "supply tab visible; settling item layout";
            return false;
        }
        if (BotMonotonicClock.ElapsedSince(_supplyPanelObservedAt).TotalMilliseconds < 800)
        {
            _hideoutStatus = "waiting for supply item layout";
            return false;
        }

        StashInventoryView.Item? target = null;
        foreach (var item in stash.Items)
        {
            if (!StashInventoryView.IsBlightRavagedMap(item) || item.Rect is null) continue;
            target = item;
            break;
        }
        if (target is null)
        {
            _supplyMissingSince = _supplyMissingSince == TimeSpan.MinValue
                ? BotMonotonicClock.Now
                : _supplyMissingSince;
            if (BotMonotonicClock.ElapsedSince(_supplyMissingSince).TotalSeconds >= 2)
            {
                StopBlightLoop(
                    $"no positively identified Blight-ravaged map in supply tab '{supplyTabName}'");
            }
            else
            {
                _hideoutStatus = "waiting for a verified Blight-ravaged map";
            }
            return false;
        }

        _supplyMissingSince = TimeSpan.MinValue;
        if (_supplyClickAttempts >= MaxSupplyClicks)
        {
            StopBlightLoop($"failed to withdraw a Blight-ravaged map after {MaxSupplyClicks} attempts");
            return false;
        }
        if (BotMonotonicClock.ElapsedSince(_lastSupplyActionAt).TotalMilliseconds < 600)
            return false;

        var rect = target.Value.Rect!.Value;
        var (x, y) = ctx.Snapshot.Window.ToScreen(
            (int)rect.CenterX, (int)rect.CenterY);
        var itemEntity = target.Value.ItemEntity;
        var ticket = ctx.Input.ModifierClick(
            x, y, [VkLeftControl], ClickIntent.InteractUi,
            "withdraw one Blight-ravaged map",
            expectResolved: () => CountCarriedBlightRavagedMaps(
                _getSnapshot()?.Inventory) == 1,
            timeoutMs: 2000);
        if (ticket.Accepted)
        {
            _supplyClickAttempts++;
            _supplyWithdrawPending = true;
            _lastSupplyActionAt = BotMonotonicClock.Now;
            _hideoutStatus = $"withdraw requested ({_supplyClickAttempts}/{MaxSupplyClicks})";
            Diagnostics.EventLog.Emit(
                "blight", "blight.supply-withdraw-requested",
                Diagnostics.EventSeverity.Info, _hideoutStatus,
                new Dictionary<string, object?>
                {
                    ["tab"] = supplyTabName,
                    ["tabIndex"] = stash.VisibleTabIndex,
                    ["itemEntity"] = $"0x{(long)itemEntity:X}",
                    ["path"] = target.Value.Path,
                });
        }
        return false;
    }

    private void StopBlightLoop(string reason)
    {
        _hideoutPhase = HideoutPhase.Stopped;
        _hideoutStatus = reason;
        _movement.Release();
        _interact.Cancel();
        _mapDevice.Cancel();
        _hideoutDeposit.Cancel();
        _supplyTabSwitcher.Reset();
        _settings.Mutate(settings => settings.BotActive = false);
        LastDecision = $"STOPPED: {reason}";
        Diagnostics.EventLog.Emit(
            "blight", "blight.loop-stopped",
            Diagnostics.EventSeverity.Warning, reason,
            new Dictionary<string, object?>
            {
                ["depositedTotal"] = _hideoutItemsDepositedTotal,
                ["mapsWithdrawnTotal"] = _mapsWithdrawnTotal,
            });
    }

    private static int CountCarriedBlightRavagedMaps(InventoryView? inventory)
    {
        if (inventory is null || !inventory.IsOpen) return 0;
        var count = 0;
        foreach (var item in inventory.Items)
            if (InventoryView.IsBlightRavagedMap(item)) count++;
        return count;
    }

    private void UpdateLiveTelemetry(BehaviorContext ctx, bool inHideout)
    {
        _countdown = ctx.Snapshot.BlightCountdown;
        _skipVisible = ctx.Snapshot.BlightSkipButton.IsVisible;
        _eligibleHostiles = 0;
        _laneMarkersSeen = 0;
        _unopenedChests = 0;
        if (ctx.Entities is not null)
        {
            foreach (var entity in ctx.Entities.Entries.Values)
            {
                if (TargetEligibility.IsEligible(entity)) _eligibleHostiles++;
                if (!string.IsNullOrEmpty(entity.Path)
                    && entity.Path.Contains("BlightPathway", StringComparison.OrdinalIgnoreCase))
                    _laneMarkersSeen++;
            }
        }

        // EntityCache is network-bubble scoped and retains stale entries. Blight reward
        // labels are the authoritative remaining-work list after completion: they are global
        // and disappear as each chest opens.
        foreach (var label in ctx.Snapshot.GroundLabels)
            if (IsBlightChestPath(label.Path)
                && BlightChestPolicy.IsEnabled(label.Path, ctx.Settings)
                && label.EntityId != 0
                && !_skippedChestIds.Contains(label.EntityId))
                _unopenedChests++;

        if (inHideout)
            _phase = _hideoutPhase == HideoutPhase.Ready
                ? $"Device/{_mapDevice.CurrentPhase}"
                : $"Hideout/{_hideoutPhase}";
        else if (_pumpAnchor is null)
            _phase = "FindPump";
        else if (_encounterStartedAt == TimeSpan.MinValue)
            _phase = _pumpReadyToStart is { IsKnown: true, Value: > 0 }
                ? "StartEncounter"
                : "NavigateToPump";
        else if (InDefendPhase(ctx))
            _phase = _timerDoneAt == TimeSpan.MinValue ? "Defend" : "WaitForStragglers";
        else if (InSweepPhase(ctx))
            _phase = "Sweep";
        else if (NeedsTerminalConfirmation(ctx))
            _phase = "ConfirmAtPump";
        else if (HasLootInRange(ctx))
            _phase = "LootGround";
        else if (_unopenedChests > 0)
            _phase = "OpenChests";
        else
            _phase = "ExitMap";

        if (!string.Equals(_phase, _lastReportedPhase, StringComparison.Ordinal))
        {
            var fromPhase = string.IsNullOrEmpty(_lastReportedPhase) ? "none" : _lastReportedPhase;
            Diagnostics.EventLog.Emit(
                "blight", "blight.phase-changed", Diagnostics.EventSeverity.Info,
                $"Blight phase {fromPhase} -> {_phase}",
                new Dictionary<string, object?>
                {
                    ["from"] = fromPhase,
                    ["to"] = _phase,
                    ["pumpEntityId"] = _pumpEntityId,
                    ["countdown"] = _countdown,
                    ["hostiles"] = _eligibleHostiles,
                    ["unopenedChests"] = _unopenedChests,
                    ["laneMarkersSeen"] = _laneMarkersSeen,
                });
            _lastReportedPhase = _phase;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private string PhaseDisplay(BehaviorContext ctx)
    {
        if (_phase == "WaitForStragglers")
        {
            var sinceTimer = Math.Max(0, (BotMonotonicClock.Now - _timerDoneAt).TotalSeconds);
            var settleRemaining = Math.Max(0, ctx.Settings.BlightPostTimerDelaySeconds - sinceTimer);
            if (settleRemaining > 0)
                return $"Waiting at pump: timer settle {settleRemaining:F0}s";

            var quiet = _lastHostileMovementAt == TimeSpan.MinValue
                ? 0
                : Math.Max(0, (BotMonotonicClock.Now - _lastHostileMovementAt).TotalSeconds);
            return $"Waiting for mobs to approach {Math.Min(quiet, ctx.Settings.BlightStuckQuietSeconds):F0}/{ctx.Settings.BlightStuckQuietSeconds:F0}s";
        }

        return _phase switch
        {
            "Sweep" => $"Final monster sweep ({_sweepGoalReason})",
            "Defend" => $"Defending pump ({_countdown ?? "timer unavailable"})",
            "OpenChests" => $"Opening reward chests ({_unopenedChests} left)",
            "LootGround" => "Collecting ground loot",
            "ExitMap" => _exitPortalAnchor is null
                ? "Returning to map exit (pump fallback)"
                : "Returning to cached map exit",
            _ => _phase,
        };
    }

    private IReadOnlyList<string> BuildHudLines()
    {
        var settings = _settings.Current;
        string primary;
        if (_phase == "WaitForStragglers" && _timerDoneAt != TimeSpan.MinValue)
        {
            var sinceTimer = Math.Max(0, (BotMonotonicClock.Now - _timerDoneAt).TotalSeconds);
            var settleRemaining = Math.Max(0, settings.BlightPostTimerDelaySeconds - sinceTimer);
            if (settleRemaining > 0)
                primary = $"Mode: Waiting at pump - settle {settleRemaining:F0}s";
            else
            {
                var quiet = _lastHostileMovementAt == TimeSpan.MinValue
                    ? 0
                    : Math.Max(0, (BotMonotonicClock.Now - _lastHostileMovementAt).TotalSeconds);
                primary = $"Mode: Waiting for mobs to approach {Math.Min(quiet, settings.BlightStuckQuietSeconds):F0}/{settings.BlightStuckQuietSeconds:F0}s";
            }
        }
        else
        {
            primary = _phase switch
            {
                "Sweep" => "Mode: Final monster sweep",
                "Defend" => "Mode: Defending pump",
                "OpenChests" => "Mode: Opening reward chests",
                "LootGround" => "Mode: Collecting ground loot",
                "ExitMap" => "Mode: Returning through map portal",
                _ => $"Mode: {_phase}",
            };
        }

        return new[]
        {
            primary,
            $"Timer: {_countdown ?? "--:--"} | Mobs: {_eligibleHostiles} | Chests: {_unopenedChests} | Maps: {_mapsCompletedTotal}",
            $"Decision: {LastDecision}",
        };
    }

    private void UpdatePumpState(BehaviorContext ctx)
    {
        if (ctx.Entities is null) return;

        // Find any pump entity (regardless of available/activated). We track its ID so we
        // can keep reading its StateMachine even after IsTargetable flips post-activation.
        EntityCache.Entry? pump = null;
        foreach (var e in ctx.Entities.Entries.Values)
        {
            if (string.IsNullOrEmpty(e.Path)) continue;
            if (!e.Path.EndsWith("/BlightPump", StringComparison.Ordinal)) continue;
            pump = e;
            break;
        }
        if (pump is null) return;

        var observationTick = ++_pumpObservationTick;
        _pumpActivated = StateMachineView.ObserveValue(ctx.Snapshot.Reader,
            pump.StateMachineCompAddr, BlightStates.Pump.Activated, observationTick, "BlightPump.Activated");
        _pumpReadyToStart = StateMachineView.ObserveValue(ctx.Snapshot.Reader,
            pump.StateMachineCompAddr, BlightStates.Pump.ReadyToStart, observationTick, "BlightPump.ReadyToStart");
        _pumpSuccess = StateMachineView.ObserveValue(ctx.Snapshot.Reader,
            pump.StateMachineCompAddr, BlightStates.Pump.Success, observationTick, "BlightPump.Success");
        _pumpFail = StateMachineView.ObserveValue(ctx.Snapshot.Reader,
            pump.StateMachineCompAddr, BlightStates.Pump.Fail, observationTick, "BlightPump.Fail");

        var wasTerminal = _terminalResolvedLatched;
        _terminalResolvedLatched = NextTerminalLatch(
            _terminalResolvedLatched,
            _pumpSuccess.IsKnown, _pumpSuccess.Value,
            _pumpFail.IsKnown, _pumpFail.Value);
        if (!wasTerminal && _terminalResolvedLatched)
        {
            Diagnostics.EventLog.Emit(
                "blight", "blight.terminal-latched", Diagnostics.EventSeverity.Info,
                $"terminal pump result latched (success={_pumpSuccess.Value}, fail={_pumpFail.Value})",
                new Dictionary<string, object?>
                {
                    ["success"] = _pumpSuccess.Value,
                    ["fail"] = _pumpFail.Value,
                    ["observationTick"] = observationTick,
                });
        }

        if (_pumpEntityId == 0)
        {
            _pumpEntityId = pump.Id;
            _pumpAnchor   = pump.GridPosition;
            BubblesBot.Bot.Diagnostics.EventLog.Log("Blight",
                $"pump found id={pump.Id} at ({pump.GridPosition.X},{pump.GridPosition.Y})");
        }

        // Read the pump's state machine to detect activation transition. activated>0 means
        // the player has clicked the pump and the encounter is live.
        if (_encounterStartedAt == TimeSpan.MinValue)
        {
            if (_pumpActivated is { IsKnown: true, Value: > 0 } activated)
            {
                _encounterStartedAt = BotMonotonicClock.Now;
                _lastEnemyAt = BotMonotonicClock.Now;
                _lastHostileMovementAt = BotMonotonicClock.Now;
                // The Blight UI displays 00:00 before activation. Only a post-activation
                // countdown observation is allowed to seed timer completion.
                _timerDoneAt = TimeSpan.MinValue;
                _sawActiveCountdown = false;
                BubblesBot.Bot.Diagnostics.EventLog.Log("Blight",
                    $"encounter active (activated={activated.Value})");
            }
        }
    }

    /// <summary>
    /// Cache the return portal while it is fresh in the network bubble. Reward lanes can
    /// lead far enough away that its entity is later evicted; exit navigation walks back to
    /// this position until a fresh portal can be clicked safely.
    /// </summary>
    private void UpdateExitPortalAnchor(BehaviorContext ctx)
    {
        if (ctx.Entities is not { } entities || ctx.Live is not { } live) return;

        EntityCache.Entry? nearest = null;
        var nearestD2 = long.MaxValue;
        foreach (var entity in entities.Entries.Values)
        {
            if (entity.IsStale) continue;
            if (!IsMapReturnPortal(entity)) continue;
            var d2 = DistanceSquared(entity.GridPosition, live.GridPosition);
            if (d2 >= nearestD2) continue;
            nearest = entity;
            nearestD2 = d2;
        }

        if (nearest is null) return;
        if (_exitPortalAnchor is { } cached
            && cached.X == nearest.GridPosition.X
            && cached.Y == nearest.GridPosition.Y) return;
        _exitPortalAnchor = nearest.GridPosition;
        Diagnostics.EventLog.Emit(
            "blight", "blight.exit-portal-cached", Diagnostics.EventSeverity.Info,
            $"cached exit portal at ({nearest.GridPosition.X},{nearest.GridPosition.Y})",
            new Dictionary<string, object?>
            {
                ["entityId"] = nearest.Id,
                ["x"] = nearest.GridPosition.X,
                ["y"] = nearest.GridPosition.Y,
            });
    }

    public static bool IsMapReturnPortal(EntityCache.Entry entity)
        => !entity.IsStale
        && !entity.Path.Contains("/BlightPortal", StringComparison.OrdinalIgnoreCase)
        && (entity.Kind == EntityListReader.EntityKind.TownPortal
            || entity.Path.Contains("MultiplexPortal", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Return the coherent observation captured once at the beginning of this world tick.
    /// </summary>
    private LongObservation ReadPumpState(int stateIndex) => stateIndex switch
    {
        BlightStates.Pump.Activated => _pumpActivated,
        BlightStates.Pump.ReadyToStart => _pumpReadyToStart,
        BlightStates.Pump.Success => _pumpSuccess,
        BlightStates.Pump.Fail => _pumpFail,
        _ => LongObservation.Unknown($"BlightPump[{stateIndex}]", _pumpObservationTick,
            ObservationReadStatus.InvalidValue),
    };

    /// <summary>True once the encounter has resolved (success or fail). Authoritative
    /// signal — pulled directly from <c>BlightPump.StateMachine.success</c> / <c>fail</c>.</summary>
    private bool IsEncounterResolved(BehaviorContext ctx)
        => _terminalResolvedLatched
        || ReadPumpState(BlightStates.Pump.Success) is { IsKnown: true, Value: > 0 }
        || ReadPumpState(BlightStates.Pump.Fail) is { IsKnown: true, Value: > 0 };

    public static bool NextTerminalLatch(
        bool currentlyLatched,
        bool successKnown,
        long successValue,
        bool failKnown,
        long failValue)
        => currentlyLatched
        || (successKnown && successValue > 0)
        || (failKnown && failValue > 0);

    private static EntityCache.Entry? PickPump(BehaviorContext ctx)
    {
        if (ctx.Entities is null || ctx.Live is null) return null;
        EntityCache.Entry? best = null;
        long bestD2 = 300L * 300L;
        foreach (var entity in ctx.Entities.Entries.Values)
        {
            if (!entity.Path.EndsWith("/BlightPump", StringComparison.Ordinal)) continue;
            long dx = entity.GridPosition.X - ctx.Live.Value.GridPosition.X;
            long dy = entity.GridPosition.Y - ctx.Live.Value.GridPosition.Y;
            var d2 = dx * dx + dy * dy;
            if (d2 < bestD2) { bestD2 = d2; best = entity; }
        }
        return best;
    }

    /// <summary>
    /// Returns the currently-locked chest target if it's still valid (in cache and not
    /// opened). Otherwise picks a new closest unopened blight chest and locks onto it. The
    /// lock prevents target thrashing as the player walks — without it, "closest" flips
    /// every tick and InteractWorldEntity never gets to finish a click sequence.
    /// </summary>
    private EntityCache.Entry? PickClosestBlightChest(BehaviorContext ctx)
    {
        if (ctx.Entities is null || ctx.Live is null) return null;

        // Honor existing lock if the chest still qualifies.
        if (_lockedChestId != 0
         && ctx.Entities.Entries.TryGetValue(_lockedChestId, out var locked)
         && !locked.IsStale
         && !locked.IsOpened
         && IsBlightChest(locked)
         && BlightChestPolicy.IsEnabled(locked.Path, ctx.Settings)
         && HasGroundLabel(ctx, locked.Id))
        {
            long dx = locked.GridPosition.X - ctx.Live.Value.GridPosition.X;
            long dy = locked.GridPosition.Y - ctx.Live.Value.GridPosition.Y;
            var clickRange = ctx.Settings.InteractionRangeGrid;
            var insideClickEnvelope = dx * dx + dy * dy <= clickRange * clickRange
                && HasTerrainAccess(ctx, locked.GridPosition);
            if (!insideClickEnvelope)
                _lockedChestAt = TimeSpan.MinValue;
            else if (_lockedChestAt == TimeSpan.MinValue)
                _lockedChestAt = BotMonotonicClock.Now;

            if (_lockedChestAt != TimeSpan.MinValue
                && (BotMonotonicClock.Now - _lockedChestAt).TotalSeconds
                    >= ChestInteractionTimeoutSeconds)
            {
                _skippedChestIds.Add(locked.Id);
                Diagnostics.EventLog.Emit(
                    "blight", "blight.chest-skipped",
                    Diagnostics.EventSeverity.Warning,
                    $"Blight chest {locked.Id} did not open within {ChestInteractionTimeoutSeconds:F0}s; continuing",
                    new Dictionary<string, object?>
                    {
                        ["entityId"] = locked.Id,
                        ["path"] = locked.Path,
                        ["gridX"] = locked.GridPosition.X,
                        ["gridY"] = locked.GridPosition.Y,
                    });
                ClearChestLock();
                return null;
            }
            return locked;
        }

        ClearChestLock();

        // Lock dropped — pick fresh and lock.
        var p = ctx.Live.Value.GridPosition;
        EntityCache.Entry? best = null;
        long bestD2 = 300L * 300L;
        foreach (var e in ctx.Entities.Entries.Values)
        {
            if (!IsBlightChest(e) || e.IsStale || e.IsOpened) continue;
            if (!BlightChestPolicy.IsEnabled(e.Path, ctx.Settings)) continue;
            if (_skippedChestIds.Contains(e.Id) || !HasGroundLabel(ctx, e.Id)) continue;
            long dx = e.GridPosition.X - p.X;
            long dy = e.GridPosition.Y - p.Y;
            var d2 = dx * dx + dy * dy;
            if (d2 < bestD2) { bestD2 = d2; best = e; }
        }

        if (best is not null && best.Id != _lockedChestId)
        {
            _lockedChestId = best.Id;
            _lockedChestAt = TimeSpan.MinValue;
            BubblesBot.Bot.Diagnostics.EventLog.Log("Blight",
                $"chest target locked id={best.Id} path={best.Path} dist={MathF.Sqrt(bestD2):F0}");
        }
        return best;
    }

    /// <summary>Closest remaining global Blight reward label. Navigation only: the fresh
    /// entity picker performs the actual click after this anchor enters the network bubble.</summary>
    private Vector2i? PickClosestBlightChestAnchor(BehaviorContext ctx)
    {
        if (ctx.Live is null) return null;
        var player = ctx.Live.Value.GridPosition;
        Vector2i? best = null;
        Vector2i? skippedBest = null;
        long bestD2 = long.MaxValue;
        long skippedBestD2 = long.MaxValue;

        foreach (var label in ctx.Snapshot.GroundLabels)
        {
            if (!IsBlightChestPath(label.Path)) continue;
            if (!BlightChestPolicy.IsEnabled(label.Path, ctx.Settings)) continue;
            if (label.EntityId == 0) continue;
            if (label.EntityGridPosition is not { } grid) continue;

            long dx = grid.X - player.X;
            long dy = grid.Y - player.Y;
            var d2 = dx * dx + dy * dy;
            if (_skippedChestIds.Contains(label.EntityId))
            {
                if (d2 < skippedBestD2) { skippedBestD2 = d2; skippedBest = grid; }
                continue;
            }
            if (d2 < bestD2) { bestD2 = d2; best = grid; }
        }

        // A failed chest is deferred while all other clusters are processed, then receives
        // one clean retry from a newly approached angle. A second failure remains skipped so
        // one genuinely blocked chest cannot wedge map completion forever.
        if (best is null && skippedBest is not null && _chestRetryPass == 0)
        {
            _chestRetryPass = 1;
            _skippedChestIds.Clear();
            Diagnostics.EventLog.Emit(
                "blight", "blight.chest-retry-pass",
                Diagnostics.EventSeverity.Info,
                "Normal chest pass complete; retrying previously failed Blight chest once");
            best = skippedBest;
        }
        return best;
    }

    private static bool HasGroundLabel(BehaviorContext ctx, uint entityId)
    {
        foreach (var label in ctx.Snapshot.GroundLabels)
            if (label.EntityId == entityId && IsBlightChestPath(label.Path)) return true;
        return false;
    }

    private void UpdateChestCompletion(BehaviorContext ctx)
    {
        if (_lockedChestId == 0 || ctx.Entities is null) return;
        if (!ctx.Entities.Entries.TryGetValue(_lockedChestId, out var chest)) return;

        var labelGone = !HasGroundLabel(ctx, chest.Id);
        if (!chest.IsOpened && !labelGone) return;

        _lastOpenedChestAnchor = chest.GridPosition;
        _chestLootDrainStartedAt = BotMonotonicClock.Now;
        _chestLootLastSeenAt = BotMonotonicClock.Now;
        _groundLootWalk.Reset();
        Diagnostics.EventLog.Emit(
            "blight", "blight.chest-open-confirmed",
            Diagnostics.EventSeverity.Info,
            $"Blight chest {chest.Id} opened; draining nearby drops before next chest",
            new Dictionary<string, object?>
            {
                ["entityId"] = chest.Id,
                ["path"] = chest.Path,
                ["chestOpenedMemory"] = chest.IsOpened,
                ["labelDisappeared"] = labelGone,
                ["gridX"] = chest.GridPosition.X,
                ["gridY"] = chest.GridPosition.Y,
            });
        ClearChestLock();
    }

    private bool IsChestLootDrainActive(BehaviorContext ctx)
    {
        if (_chestLootDrainStartedAt == TimeSpan.MinValue) return false;
        var now = BotMonotonicClock.Now;
        if ((now - _chestLootDrainStartedAt).TotalSeconds >= ChestLootMaxDrainSeconds)
        {
            Diagnostics.EventLog.Emit(
                "blight", "blight.chest-loot-drain-timeout",
                Diagnostics.EventSeverity.Warning,
                "Chest drop drain reached its 30s safety limit; continuing to the next chest");
            EndChestLootDrain();
            return false;
        }

        if (PickClosestChestDropAnchor(ctx) is not null)
        {
            _chestLootLastSeenAt = now;
            return true;
        }

        if ((now - _chestLootLastSeenAt).TotalSeconds < ChestLootQuietSeconds)
            return true;

        Diagnostics.EventLog.Emit(
            "blight", "blight.chest-loot-drained",
            Diagnostics.EventSeverity.Info,
            "Nearby chest drop cluster drained; selecting the next chest");
        EndChestLootDrain();
        return false;
    }

    private Vector2i? PickClosestChestDropAnchor(BehaviorContext ctx)
    {
        if (_lastOpenedChestAnchor is not { } chestAnchor || ctx.Live is not { } live)
            return null;

        Vector2i? best = null;
        long bestD2 = long.MaxValue;
        var clusterR2 = (long)ChestLootClusterRadiusGrid * ChestLootClusterRadiusGrid;
        foreach (var label in ctx.Snapshot.GroundLabels)
        {
            if (!ShouldTakeGroundItem(ctx, label)) continue;
            if (label.EntityGridPosition is not { } grid) continue;
            long cx = grid.X - chestAnchor.X, cy = grid.Y - chestAnchor.Y;
            if (cx * cx + cy * cy > clusterR2) continue;

            long dx = grid.X - live.GridPosition.X, dy = grid.Y - live.GridPosition.Y;
            var d2 = dx * dx + dy * dy;
            if (d2 < bestD2) { bestD2 = d2; best = grid; }
        }
        return best;
    }

    private void EndChestLootDrain()
    {
        _chestLootDrainStartedAt = TimeSpan.MinValue;
        _chestLootLastSeenAt = TimeSpan.MinValue;
        _lastOpenedChestAnchor = null;
        _groundLootWalk.Reset();
    }

    private static bool HasTerrainAccess(BehaviorContext ctx, Vector2i target)
    {
        if (ctx.Live is not { } live) return false;
        var targeting = ctx.Snapshot.Nav.TargetingReader;
        return targeting is null || BubblesBot.Core.Pathfinding.PathSmoother.HasLineOfSight(
            targeting,
            live.GridPosition.X, live.GridPosition.Y,
            target.X, target.Y,
            minValue: 1);
    }

    private void ClearChestLock()
    {
        _lockedChestId = 0;
        _lockedChestAt = TimeSpan.MinValue;
    }

    private static bool IsBlightChest(EntityCache.Entry e)
        => e.Kind == EntityListReader.EntityKind.Chest
        && IsBlightChestPath(e.Path);

    public static bool IsBlightChestPath(string? path)
        => !string.IsNullOrEmpty(path)
        && path.StartsWith("Metadata/Chests/Blight", StringComparison.OrdinalIgnoreCase);


    private bool InDefendPhase(BehaviorContext ctx)
    {
        if (_pumpAnchor is null || _encounterStartedAt == TimeSpan.MinValue) return false;
        // Authoritative end signal — pump's StateMachine flips success or fail.
        if (IsEncounterResolved(ctx)) return false;
        var elapsed = (BotMonotonicClock.Now - _encounterStartedAt).TotalSeconds;
        if (elapsed > ctx.Settings.BlightDefendTimeoutSeconds) return false;

        // Timer-done + stuck handoff. UI countdown reaching 0:00 means no new mobs will
        // spawn, but stragglers may still be walking toward the pump. Two gates in order:
        //   1. Post-timer delay — hold position for a fixed configurable window so the
        //      pump's state machine + late-arrival mobs have time to resolve.
        //   2. Quiet window — once the delay elapses, only transition once nothing's been
        //      seen moving for N consecutive seconds.
        // Together this catches "all mobs killed" and "remaining mobs are stuck on terrain"
        // without leaving the pump prematurely while live mobs are still en route.
        if (_timerDoneAt != TimeSpan.MinValue)
        {
            var sinceTimer = (BotMonotonicClock.Now - _timerDoneAt).TotalSeconds;
            var quietFor = _lastHostileMovementAt == TimeSpan.MinValue
                ? 0.0
                : (BotMonotonicClock.Now - _lastHostileMovementAt).TotalSeconds;
            return ShouldRemainInDefendAfterTimer(
                cleanupStarted: _sweepStartedAt != TimeSpan.MinValue,
                sinceTimer,
                ctx.Settings.BlightPostTimerDelaySeconds,
                quietFor,
                ctx.Settings.BlightStuckQuietSeconds);
        }
        return true;
    }

    /// <summary>
    /// Once cleanup starts it is a one-way phase handoff. A moving straggler must not reset
    /// the quiet clock and pull the bot back to the pump; cleanup will pursue that monster.
    /// </summary>
    public static bool ShouldRemainInDefendAfterTimer(
        bool cleanupStarted,
        double sinceTimer,
        double postTimerDelay,
        double quietFor,
        double stuckQuietSeconds)
    {
        if (cleanupStarted) return false;
        if (sinceTimer <= postTimerDelay) return true;
        return quietFor <= stuckQuietSeconds;
    }

    /// <summary>True while the pump entity exists, hasn't been clicked yet, and its
    /// <c>ready_to_start</c> flag is still 0. This is the "walk into range to arm the
    /// encounter UI" window — the StateMachine doesn't populate <c>ready_to_start</c>
    /// until the player gets close enough.</summary>
    private bool PumpNeedsApproach(BehaviorContext ctx)
    {
        if (_pumpEntityId == 0 || !_pumpAnchor.HasValue) return false;
        var activated = ReadPumpState(BlightStates.Pump.Activated);
        var ready = ReadPumpState(BlightStates.Pump.ReadyToStart);
        if (!activated.IsKnown || !ready.IsKnown) return false;
        if (activated.Value > 0) return false;
        if (ready.Value > 0) return false;
        return true;
    }

    /// <summary>
    /// Click the blight skip button (fast-forward) if it's visible and we haven't already
    /// clicked it this encounter. The button only appears between pump activation and the
    /// first wave; clicking it skips the pre-wave wait. Throttled so we don't spam if the
    /// click hasn't taken effect yet (animations / latency).
    /// </summary>
    private void TryClickSkipButton(BehaviorContext ctx)
    {
        if (!ctx.Settings.BlightClickSkipButton) return;
        if (_skipButtonClicked) return;

        var btn = ctx.Snapshot.BlightSkipButton;
        if (!btn.IsVisible || btn.ClickRect is not { } rect) return;
        if (rect.Width <= 0 || rect.Height <= 0) return;

        // Throttle: don't re-click within 1.5s of the last attempt. After ~3s of visible
        // button + no state change we give up and let the bot wait out the timer naturally.
        var sinceClick = _lastSkipClickAt == TimeSpan.MinValue
            ? double.PositiveInfinity
            : (BotMonotonicClock.Now - _lastSkipClickAt).TotalMilliseconds;
        if (sinceClick < 1500) return;

        if (_lastSkipClickAt != TimeSpan.MinValue
            && (BotMonotonicClock.Now - _lastSkipClickAt).TotalSeconds > 3)
        {
            // Tried, didn't take, button still up — most likely an off-encounter false
            // positive. Mark clicked to stop trying for the rest of this encounter.
            _skipButtonClicked = true;
            BubblesBot.Bot.Diagnostics.EventLog.Log("Blight",
                "skip-button click never took — giving up for this encounter");
            return;
        }

        var abs = ctx.Snapshot.Window.ToScreen((int)rect.CenterX, (int)rect.CenterY);
        var ticket = ctx.Input.Click(abs.X, abs.Y,
            ClickIntent.InteractUi, "blight skip",
            // The captured `ctx` snapshot is stale by the time the gate polls this callback
            // — re-read via the live snapshot accessor so we observe the button after the
            // click has had time to land.
            expectResolved: () =>
            {
                var snap = _getSnapshot();
                return snap is null || !snap.BlightSkipButton.IsVisible;
            },
            timeoutMs: 1200);
        if (ticket.Accepted)
        {
            _lastSkipClickAt = BotMonotonicClock.Now;
            // Mark clicked optimistically — if the click was suppressed by another in-flight
            // input, throttle covers retry. If the click went through, the button vanishes
            // on the next snapshot read and we never re-enter this method.
            _skipButtonClicked = true;
            BubblesBot.Bot.Diagnostics.EventLog.Log("Blight",
                $"skip-button clicked at ({abs.X},{abs.Y})");
        }
    }

    /// <summary>True when the pump is ready to be clicked: ready_to_start flag set and
    /// activated still zero. Drives the pump-click selector branch.</summary>
    private bool PumpNeedsClick(BehaviorContext ctx)
    {
        if (_pumpEntityId == 0) return false;
        var activated = ReadPumpState(BlightStates.Pump.Activated);
        var ready = ReadPumpState(BlightStates.Pump.ReadyToStart);
        if (!activated.IsKnown || !ready.IsKnown) return false;
        if (activated.Value > 0) return false;
        if (ready.Value <= 0) return false;
        return PickPump(ctx) is not null;
    }

    /// <summary>
    /// Walk every cached hostile in the network bubble; if any has <c>IsMoving=true</c>,
    /// stamp <see cref="_lastHostileMovementAt"/> to now. The freeze-window between updates
    /// is the "everything's dead or stuck" signal that gates the post-timer sweep.
    /// </summary>
    private void UpdateHostileMovementTracking(BehaviorContext ctx)
    {
        if (ctx.Entities is null || ctx.Live is null) return;
        var p = ctx.Live.Value.GridPosition;
        // ~180 grid is the conservative network-bubble radius PoE keeps fully synchronized.
        const float bubbleR = 180f;
        var r2 = bubbleR * bubbleR;

        foreach (var e in ctx.Entities.Entries.Values)
        {
            if (!TargetEligibility.IsEligible(e)) continue;
            var dx = (float)(e.GridPosition.X - p.X);
            var dy = (float)(e.GridPosition.Y - p.Y);
            if (dx * dx + dy * dy > r2) continue;
            if (e.IsMoving)
            {
                _lastHostileMovementAt = BotMonotonicClock.Now;
                return;
            }
        }

        // No hostile observed moving this tick. If the timestamp is unset, anchor it now so
        // the "stuck for N seconds" gate has a defined start — otherwise the gate would
        // false-fire the very first time the bot enters a quiet area.
        if (_lastHostileMovementAt == TimeSpan.MinValue)
            _lastHostileMovementAt = BotMonotonicClock.Now;
    }

    /// <summary>True while post-timer monster search is running. Once started it remains
    /// active through moving-monster observations, pursues every fresh target, and ends only
    /// after reachable exploration is exhausted with no monster remaining.</summary>
    private bool InSweepPhase(BehaviorContext ctx)
    {
        if (_pumpAnchor is null) return false;
        if (_encounterStartedAt == TimeSpan.MinValue) return false;
        // Sweep activates only after defend has actually ended (i.e. InDefendPhase returns
        // false). Mirroring its exit conditions here keeps the two predicates aligned —
        // no gap tick where neither is active.
        var elapsed = (BotMonotonicClock.Now - _encounterStartedAt).TotalSeconds;
        var quietFor = _lastHostileMovementAt == TimeSpan.MinValue
            ? 0.0
            : (BotMonotonicClock.Now - _lastHostileMovementAt).TotalSeconds;
        var sinceTimer = _timerDoneAt == TimeSpan.MinValue
            ? 0.0
            : (BotMonotonicClock.Now - _timerDoneAt).TotalSeconds;
        // Defend → sweep handoff requires BOTH the post-timer settle delay AND the quiet
        // window. Mirrors InDefendPhase's exit logic exactly so there's no tick where
        // neither predicate is active.
        var timerHandoff = _timerDoneAt != TimeSpan.MinValue
                        && sinceTimer > ctx.Settings.BlightPostTimerDelaySeconds
                        && quietFor > ctx.Settings.BlightStuckQuietSeconds;
        var resolved = IsEncounterResolved(ctx);
        var timedOut = elapsed > ctx.Settings.BlightDefendTimeoutSeconds;
        if (!ShouldKeepPostEncounterCleanup(
                cleanupStarted: _sweepStartedAt != TimeSpan.MinValue,
                resolved,
                timedOut,
                timerHandoff))
            return false;

        // A positive pump success/fail result is authoritative and bypasses this sweep.
        // Pathway cleanup exists only for an unresolved timer-end/timeout where monsters may
        // be stuck. Reward chest labels/entities are already exposed after success and the
        // chest navigator provides the necessary physical coverage while opening them.

        if (ctx.Settings.BlightPostSweepSeconds <= 0) return false;

        if (_sweepStartedAt == TimeSpan.MinValue) return true;   // first tick — let the sweep action start it

        // Progress, not a wall-clock cap, ends cleanup. Keep a fresh monster locked until it
        // dies; otherwise expand the network bubble until reachable exploration is exhausted.
        var secondsSinceLastEnemy = (BotMonotonicClock.Now - _sweepLastEnemyAt).TotalSeconds;
        return ShouldContinueSweepAfterQuietWindow(
            HasEligibleHostile(ctx),
            _cleanupExploration.IsExhausted,
            secondsSinceLastEnemy);
    }

    public static bool ShouldContinueSweepAfterQuietWindow(
        bool hasEligibleHostile,
        bool explorationExhausted,
        double secondsSinceLastEnemy)
        => hasEligibleHostile || !explorationExhausted || secondsSinceLastEnemy <= 2.0;

    public static bool ShouldEnterPostEncounterSweep(
        bool encounterResolved,
        bool defendTimedOut,
        bool timerQuietHandoff)
        => !encounterResolved && (defendTimedOut || timerQuietHandoff);

    public static bool ShouldKeepPostEncounterCleanup(
        bool cleanupStarted,
        bool encounterResolved,
        bool defendTimedOut,
        bool timerQuietHandoff)
        => !encounterResolved && (cleanupStarted || defendTimedOut || timerQuietHandoff);

    private bool NeedsTerminalConfirmation(BehaviorContext ctx)
        => _encounterStartedAt != TimeSpan.MinValue
        && !IsEncounterResolved(ctx)
        && !InDefendPhase(ctx)
        && !InSweepPhase(ctx);

    /// <summary>Stable monster target for cleanup pursuit. When this returns null, the
    /// separate exploration frontier expands the network bubble to discover another mob.</summary>
    private Vector2i? GetSweepGoal(BehaviorContext ctx)
    {
        if (ctx.Entities is not { } cache || ctx.Live is not { } live)
        {
            _sweepTargetId = 0;
            _sweepGoal = null;
            _sweepGoalReason = "no-live-cache";
            return null;
        }

        if (_sweepTargetId != 0
            && cache.Entries.TryGetValue(_sweepTargetId, out var locked)
            && TargetEligibility.IsEligible(locked))
        {
            _sweepGoal = BlightPositioning.ApproachAtStandoff(
                live.GridPosition, locked.GridPosition, ctx.Settings.BlightCleanupStandoffRadius);
            _sweepGoalReason = $"hostile-lock:{locked.Id}:standoff={ctx.Settings.BlightCleanupStandoffRadius:F0}";
            return _sweepGoal;
        }

        EntityCache.Entry? nearestHostile = null;
        long nearestHostileD2 = long.MaxValue;
        foreach (var entity in cache.Entries.Values)
        {
            if (!TargetEligibility.IsEligible(entity)) continue;
            var d2 = DistanceSquared(entity.GridPosition, live.GridPosition);
            if (d2 >= nearestHostileD2) continue;
            nearestHostileD2 = d2;
            nearestHostile = entity;
        }
        if (nearestHostile is not null)
        {
            _sweepTargetId = nearestHostile.Id;
            _sweepGoal = BlightPositioning.ApproachAtStandoff(
                live.GridPosition, nearestHostile.GridPosition, ctx.Settings.BlightCleanupStandoffRadius);
            _sweepGoalReason = $"hostile-acquired:{nearestHostile.Id}:standoff={ctx.Settings.BlightCleanupStandoffRadius:F0}";
            return _sweepGoal;
        }

        // Continuous angular sweep based on elapsed time — eliminates "stuck on one waypoint"
        // failure modes. ~one full orbit every 12s. Radius oscillates slightly (sin) within
        // a band based on the configured defend radius so the orbit hugs the pump's lane area.
        _sweepTargetId = 0;
        _sweepGoal = null;
        _sweepGoalReason = "explore";
        return null;
    }

    private static bool HasEligibleHostile(BehaviorContext ctx)
    {
        if (ctx.Entities is null) return false;
        foreach (var entity in ctx.Entities.Entries.Values)
            if (TargetEligibility.IsEligible(entity)) return true;
        return false;
    }

    private static long DistanceSquared(Vector2i a, Vector2i b)
    {
        long dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private bool NeedsToReturnToPump(BehaviorContext ctx)
    {
        return GetDefendGoal(ctx) is not null;
    }

    private Vector2i? GetDefendGoal(BehaviorContext ctx)
    {
        if (_pumpAnchor is null || ctx.Live is null || ctx.Entities is null)
        {
            _defendGoalReason = "hold-pump";
            return null;
        }

        var choice = BlightPositioning.Choose(
            _pumpAnchor.Value,
            ctx.Live.Value.GridPosition,
            ctx.Settings.BlightDefendRadius,
            ctx.Entities.Entries.Values);
        _defendGoalReason = choice?.Reason ?? "hold-pump";
        return choice?.Position;
    }

    /// <summary>
    /// True when there's a map device entity in the cache — the canonical "we're in a
    /// hideout / town with the device available" signal. Positive evidence rather than
    /// absence-of-blight, so it doesn't false-negative when leftover blight entities from
    /// the previous map are still in the cache during the hideout-arrival lag.
    /// </summary>
    private static bool IsLikelyInHideout(BehaviorContext ctx)
    {
        if (ctx.Entities is null) return false;
        foreach (var e in ctx.Entities.Entries.Values)
        {
            if (e.Name == "Map Device") return true;
            if (!string.IsNullOrEmpty(e.Path)
                && e.Path.Contains("MappingDevice", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static SkillSlot? PickAttackSlot(BotSettings settings)
    {
        foreach (var s in settings.Skills.Slots)
            if (s.Role == SkillRole.Attack && s.Vk != 0) return s;
        return null;
    }

    private void MaintainCombatUtilities(BehaviorContext ctx)
    {
        // Utilities share the same input gate and SkillBook as Q. A successful W/R/T tap wins
        // this tick and Q resumes on the next. Spirit Offering is aimed at self so PoE can
        // select a nearby corpse; Vaal Haste is harmless until souls are available; marks are
        // restricted to rare/unique targets by MaintainTargetedMark.
        _combatBuffs.Tick(ctx);
        _combatMark.Tick(ctx);
    }

    private static bool LowHp(BehaviorContext ctx)
    {
        var live = ctx.Live;
        if (live is null || live.Value.HpMax <= 0) return false;
        var th = ctx.Settings.HpRetreatThreshold;
        if (th <= 0) return false;
        return (float)live.Value.HpCurrent / live.Value.HpMax < th;
    }

    private static bool InAttackRange(BehaviorContext ctx)
    {
        var slot = PickAttackSlot(ctx.Settings);
        if (slot is null || ctx.Live is null || ctx.Entities is null) return false;
        var p = ctx.Live.Value.GridPosition;
        var r2 = (float)slot.MaxRangeGrid * slot.MaxRangeGrid;
        foreach (var e in ctx.Entities.Entries.Values)
        {
            if (!TargetEligibility.IsEligible(e)) continue;
            if (EnemyIgnoreList.IsIgnored(e.Name)) continue;
            var dx = (float)(e.GridPosition.X - p.X);
            var dy = (float)(e.GridPosition.Y - p.Y);
            if (dx * dx + dy * dy <= r2) return true;
        }
        return false;
    }

    private static bool HasLootInRange(BehaviorContext ctx)
    {
        var r = MathF.Min(25f, ctx.Settings.LootRangeGrid);
        foreach (var l in ctx.Snapshot.GroundLabels)
        {
            if (!ShouldTakeGroundItem(ctx, l)) continue;
            if (l.EntityGridPosition is not { } grid) continue;
            if (l.DistanceToPlayer <= r && HasTerrainAccess(ctx, grid)) return true;
        }
        return false;
    }

    private static bool ShouldTakeGroundItem(BehaviorContext ctx, GroundLabelView label)
    {
        if (!label.IsItem || !label.IsLabelVisible) return false;
        var filter = LootClosestVisible.SharedValueFilter;
        return filter is null || filter.Evaluate(label, ctx.Settings.Loot).ShouldTake;
    }
}
