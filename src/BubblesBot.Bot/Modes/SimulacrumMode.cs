using BubblesBot.Bot.Behaviors;
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
/// Production in-map Simulacrum adapter. It remains capture-safe when the current-build
/// <see cref="SimulacrumStates.Monolith"/> contract is unvalidated: raw monolith telemetry is
/// published, but no input is emitted.
/// </summary>
public sealed class SimulacrumMode : IBotMode
{
    private const int MaxWaves = 15;
    private const int DrySweepsBeforePortalRefresh = 8;
    private const int MaxPortalRefreshAttemptsPerWave = 2;
    private const int ArenaRevealRadiusGrid = 25;
    private const int VkEscape = 0x1B;

    private readonly SettingsStore _settings;
    private readonly Func<GameSnapshot?> _getSnapshot;
    private readonly Func<LivePlayer?> _getLive;
    private readonly Func<EntityCache?> _getEntities;
    private readonly CombatCoordinator _coord;
    private readonly MovementSystem _movement;
    private readonly CombatSystem _combat;
    private readonly FlaskSystem _flasks;
    private readonly InteractSystem _interact = new();
    private readonly SkillBook _skills;
    private readonly LootClosestVisible _loot;
    private readonly ExplorationSystem _exploration = new(ArenaRevealRadiusGrid);
    private readonly FollowPath _fightPath;
    private readonly FollowPath _lootSweepPath;
    private readonly FollowPath _returnToMonolith;
    private readonly InteractWorldEntity _startWave;
    private readonly StashDepositSystem _stash;
    private readonly EnterAreaTransition _exit;
    private readonly SimulacrumRecoveryStore _recovery = new();
    private readonly IBehavior _root;

    private SimulacrumController _controller;
    private EntityCache.Entry? _monolith;
    private Vector2i? _monolithAnchor;
    private uint _monolithId;
    private Vector2i? _stashAnchor;
    private uint _stashId;
    private Vector2i? _portalAnchor;
    private uint _portalId;
    private bool _lastActiveKnown;
    private bool _lastActive;
    private bool _sawActiveWave;
    private int _activeWave;
    private TimeSpan _canStartWaveAt = TimeSpan.MinValue;
    private TimeSpan _lootQuietSince = TimeSpan.MinValue;
    private bool _lootActionableLastTick;
    private readonly HashSet<int> _depositedWaves = new();
    private SimulacrumPhase _lastLoggedPhase = (SimulacrumPhase)(-1);
    private SimulacrumCommand _lastLoggedCommand = (SimulacrumCommand)(-1);
    private string _fatalReason = string.Empty;
    private int _waveSweepPass;
    private TimeSpan _nextSweepResetAt = TimeSpan.MinValue;
    private InventoryProbePhase _inventoryProbe;
    private TimeSpan _inventoryProbeAt = TimeSpan.MinValue;
    private int _inventoryProbeWave;
    private int _inventoryOccupiedCells;
    private bool _inventoryEstimateKnown;
    private bool _inventoryEstimateExact;
    private string _inventoryOccupancySource = "unknown";
    private readonly Dictionary<uint, PendingRewardLoot> _pendingRewardLoot = new();
    private Vector2i? _rewardDropAnchor;
    private TimeSpan _rewardSettleUntil = TimeSpan.MinValue;
    private string _combatDestination = "none";
    private uint _recoveryAreaHash;
    private bool _recoveryLoaded;
    private string _lastRecoverySignature = string.Empty;
    private string _recoveryStatus = "not loaded";
    private bool _reattachRewardSweepRequired;
    private bool _lootRecoverySweep;
    private int _deathCount;
    private bool _portalRefreshRequested;
    private int _portalRefreshAttempts;
    private int _portalRefreshAttemptWave;

    private enum InventoryProbePhase { Idle, Opening, Reading, Closing }
    private readonly record struct PendingRewardLoot(Vector2i Position, string Name);

    public string Name => "Simulacrum";
    public IBehavior Root => _root;
    public string LastDecision { get; private set; } = "init";
    public string? RunId { get; private set; }
    public SimulacrumPhase Phase => _controller.Phase;
    public bool IsFailed => _controller.Phase == SimulacrumPhase.Failed
        || !string.IsNullOrEmpty(_fatalReason);
    public string FailureReason => !string.IsNullOrEmpty(_fatalReason)
        ? _fatalReason
        : _controller.TerminalReason;
    public bool PortalRefreshRequested => _portalRefreshRequested;
    public int ActiveWave => _activeWave;
    public int WaveSweepPass => _waveSweepPass;
    public Vector2i? PortalAnchor => _portalAnchor;
    public TimeSpan ActiveWaveElapsed => _controller.ActiveWaveElapsed(BotMonotonicClock.Now);
    public TimeSpan ActiveWaveLimit => _controller.WaveTimeout;
    public void RestartActiveWaveClock()
        => _controller.RestartActiveWaveClock(BotMonotonicClock.Now);
    public object Telemetry => new
    {
        contractValidated = SimulacrumStates.Monolith.IsValidated,
        entityId = _monolith?.Id ?? 0,
        anchor = _monolithAnchor is { } p ? new { x = p.X, y = p.Y } : null,
        stash = _stashAnchor is { } s ? new { id = _stashId, x = s.X, y = s.Y } : null,
        portal = _portalAnchor is { } t ? new { id = _portalId, x = t.X, y = t.Y } : null,
        rawStates = _monolith?.SimulacrumRawStates ?? Array.Empty<long>(),
        active = _monolith?.SimulacrumActive,
        goodbye = _monolith?.SimulacrumGoodbye,
        wave = _monolith?.SimulacrumWave,
        phase = _controller.Phase.ToString(),
        decision = LastDecision,
        activeWave = _activeWave,
        deaths = _deathCount,
        waveElapsedSeconds = ActiveWaveElapsed.TotalSeconds,
        waveLimitSeconds = ActiveWaveLimit.TotalSeconds,
        waveRemainingSeconds = Math.Max(0,
            (ActiveWaveLimit - ActiveWaveElapsed).TotalSeconds),
        portalRefreshRequested = _portalRefreshRequested,
        portalRefreshAttempts = _portalRefreshAttempts,
        portalRefreshAttemptWave = _portalRefreshAttemptWave,
        maxPortalRefreshAttemptsPerWave = MaxPortalRefreshAttemptsPerWave,
        waveSweepPass = _waveSweepPass,
        canStartAtMs = _canStartWaveAt.TotalMilliseconds,
        depositedWaves = _depositedWaves.Order().ToArray(),
        inventoryOccupiedCells = _inventoryOccupiedCells,
        inventoryEstimateKnown = _inventoryEstimateKnown,
        inventoryEstimateExact = _inventoryEstimateExact,
        inventoryOccupancySource = _inventoryOccupancySource,
        inventoryProbe = _inventoryProbe.ToString(),
        lootDecision = _loot.LastDecision,
        lootSweepDecision = _lootSweepPath.LastDecision,
        lootSweepGoal = _lootSweepPath.Goal is { } lootGoal
            ? new { x = lootGoal.X, y = lootGoal.Y }
            : null,
        interactionBusy = _interact.IsBusy,
        startWaveDecision = _startWave.LastDecision,
        pendingRewardLoot = _pendingRewardLoot.Count,
        pendingRewardNames = _pendingRewardLoot.Values.Select(x => x.Name).Take(20).ToArray(),
        pendingRewardItems = _pendingRewardLoot.Select(x => new
        {
            id = x.Key,
            name = x.Value.Name,
            x = x.Value.Position.X,
            y = x.Value.Position.Y,
        }).Take(20).ToArray(),
        rewardDropAnchor = _rewardDropAnchor is { } r ? new { x = r.X, y = r.Y } : null,
        rewardSettleUntilMs = _rewardSettleUntil.TotalMilliseconds,
        combatDestination = _combatDestination,
        fightPathDecision = _fightPath.LastDecision,
        fightPathStatus = _fightPath.LastStatus.ToString(),
        fightPathGoal = _fightPath.Goal is { } fightGoal
            ? new { x = fightGoal.X, y = fightGoal.Y }
            : null,
        recoveryStatus = _recoveryStatus,
        reattachRewardSweepRequired = _reattachRewardSweepRequired,
        lootRecoverySweep = _lootRecoverySweep,
        fatalReason = _fatalReason,
    };

    public SimulacrumMode(SettingsStore settings, CombatCoordinator coord,
        Func<GameSnapshot?> getSnapshot,
        Func<LivePlayer?> getLive, Func<EntityCache?> getEntities)
    {
        _settings = settings;
        _getSnapshot = getSnapshot;
        _getLive = getLive;
        _getEntities = getEntities;
        // Shared combat brain: repoint the local combat systems at the coordinator's instances
        // so movement/skills/channels are one authority across modes.
        _coord = coord;
        _movement = coord.Movement;
        _combat = coord.Combat;
        _flasks = coord.Flasks;
        _skills = coord.Skills;
        _loot = new LootClosestVisible("simulacrum loot", _interact, getSnapshot);
        _loot.PickupConfirmed += OnPickupConfirmed;
        _stash = new StashDepositSystem(
            _movement,
            _skills,
            getSnapshot,
            stashFallbackGoal: _ => _stashAnchor);

        _fightPath = new FollowPath("simulacrum engage/explore", _movement,
            FindFightGoal, _skills, goalArrivalRadius: 6f);
        _lootSweepPath = new FollowPath("simulacrum loot sweep", _movement,
            FindLootSweepGoal, _skills, goalArrivalRadius: 6f);
        _returnToMonolith = new FollowPath("simulacrum return to monolith", _movement,
            _ => _monolithAnchor, _skills, goalArrivalRadius: 20f);
        _startWave = new InteractWorldEntity("simulacrum monolith", _interact,
            _movement, _skills, FindMonolith,
            (_, entity) => IsWaveActive(entity));
        _exit = new EnterAreaTransition("simulacrum exit", _interact, _movement,
            _skills, getSnapshot,
            entity => entity.Kind == EntityListReader.EntityKind.TownPortal,
            _ => _portalAnchor);
        _root = new Behaviors.Action("simulacrum lifecycle", TickLifecycle);
        _controller = CreateController(settings.Current);
    }

    public void Tick(GameSnapshot snapshot, IInputRouter input)
    {
        _coord.BeginTick(snapshot);

        var ctx = new BehaviorContext(snapshot, input, _settings.Current, _getLive(), _getEntities());
        if (snapshot.Inventory.IsOpen && _inventoryProbe == InventoryProbePhase.Idle)
            SetExactInventoryOccupancy(snapshot.Inventory.OccupiedCells, "visible inventory");
        UpdateLandmarks(ctx);

        if (!SimulacrumStates.Monolith.IsValidated)
        {
            _movement.Release();
            _combat.StopAllChannels();
            LastDecision = _monolith is null
                ? "capture-safe: waiting for Afflictionator monolith"
                : $"capture-safe: raw monolith states [{string.Join(',', _monolith.SimulacrumRawStates)}]";
            return;
        }

        _coord.PreRoot(ctx);
        _root.Tick(ctx);
        var combatResult = _coord.PostRoot(ctx);
        if (combatResult.FatalReason is { } fatal && string.IsNullOrEmpty(_fatalReason))
            _fatalReason = $"combat: {fatal}";
    }

    public void Reset()
    {
        _coord.ResetCombat();
        _movement.Release();
        _interact.Cancel();
        _loot.Reset();
        _exploration.Reset();
        _fightPath.Reset();
        _lootSweepPath.Reset();
        _returnToMonolith.Reset();
        _startWave.Reset();
        _stash.Cancel();
        _exit.Reset();
        _controller = CreateController(_settings.Current);
        _monolith = null;
        _monolithAnchor = null;
        _monolithId = 0;
        _stashAnchor = null;
        _stashId = 0;
        _portalAnchor = null;
        _portalId = 0;
        _lastActiveKnown = false;
        _lastActive = false;
        _sawActiveWave = false;
        _activeWave = 0;
        _canStartWaveAt = TimeSpan.MinValue;
        _lootQuietSince = TimeSpan.MinValue;
        _lootActionableLastTick = false;
        _depositedWaves.Clear();
        _lastLoggedPhase = (SimulacrumPhase)(-1);
        _lastLoggedCommand = (SimulacrumCommand)(-1);
        _fatalReason = string.Empty;
        _waveSweepPass = 0;
        _nextSweepResetAt = TimeSpan.MinValue;
        _inventoryProbe = InventoryProbePhase.Idle;
        _inventoryProbeAt = TimeSpan.MinValue;
        _inventoryProbeWave = 0;
        _inventoryOccupiedCells = 0;
        _inventoryEstimateKnown = false;
        _inventoryEstimateExact = false;
        _inventoryOccupancySource = "unknown";
        _pendingRewardLoot.Clear();
        _rewardDropAnchor = null;
        _rewardSettleUntil = TimeSpan.MinValue;
        _combatDestination = "none";
        _recoveryAreaHash = 0;
        _recoveryLoaded = false;
        _lastRecoverySignature = string.Empty;
        _recoveryStatus = "not loaded";
        _reattachRewardSweepRequired = false;
        _lootRecoverySweep = false;
        _deathCount = 0;
        _portalRefreshRequested = false;
        _portalRefreshAttempts = 0;
        _portalRefreshAttemptWave = 0;
        RunId = null;
        LastDecision = "reset";
    }

    /// <summary>
    /// Reset transient combat/controller state after leaving and re-entering the same arena,
    /// while retaining instance-stable landmark coordinates needed to get back into network
    /// range. Entity IDs are deliberately discarded because they can change on re-entry.
    /// </summary>
    public void ResetForReattach(bool preserveSweepProgress = false)
    {
        var monolith = _monolithAnchor;
        var stash = _stashAnchor;
        var portal = _portalAnchor;
        // Re-entering the same instance does not begin a new wave. Preserve the controller
        // so the hard active-wave clock survives checkpoint deaths and portal refreshes.
        // A process restart still constructs a fresh controller and therefore a fresh clock.
        var controller = _controller;
        var portalRefreshAttempts = _portalRefreshAttempts;
        var portalRefreshAttemptWave = _portalRefreshAttemptWave;
        var waveSweepPass = preserveSweepProgress ? _waveSweepPass : 0;
        Reset();
        _controller = controller;
        _monolithAnchor = monolith;
        _stashAnchor = stash;
        _portalAnchor = portal;
        _portalRefreshAttempts = portalRefreshAttempts;
        _portalRefreshAttemptWave = portalRefreshAttemptWave;
        _waveSweepPass = waveSweepPass;
    }

    private BehaviorStatus TickLifecycle(BehaviorContext ctx)
    {
        UpdateLandmarks(ctx);
        // Terminal state belongs to the completed arena. Once the area transition clears
        // that arena's entity cache, latch completion by disarming instead of letting the
        // no-monolith branch explore the destination hideout as if it were a fresh arena.
        if (_controller.Phase == SimulacrumPhase.Terminal
            && RunId is not null
            && _monolith is null)
        {
            _movement.Release();
            _settings.Mutate(settings => settings.BotActive = false);
            LastDecision = "Terminal/Complete: exited arena; automation disarmed";
            return BehaviorStatus.Success;
        }

        LoadRecovery(ctx);
        if (!string.IsNullOrEmpty(_fatalReason))
        {
            _movement.Release();
            LastDecision = $"STOP: {_fatalReason}";
            return BehaviorStatus.Running;
        }

        // Fresh instances can spawn the monolith outside the initial network bubble. Search
        // the arena physically until it is observed; absence is never treated as completion.
        if (_monolith is null)
        {
            if (ctx.Live is { } locating)
                _exploration.TrackVisit(ctx.Snapshot, locating.GridPosition);
            _fightPath.Tick(ctx);
            LastDecision = $"LocateStart: {_exploration.LastFrontierReason}";
            return BehaviorStatus.Running;
        }

        var wave = ReadWave(_monolith);
        var active = ObserveActive(_monolith);
        var firstFreshActiveObservation = !_lastActiveKnown && active.IsKnown;
        ObserveActiveEdge(active, wave, ctx);
        if (firstFreshActiveObservation && !active.IsTrue && wave > 0)
        {
            // We may have crashed before a newly-visible reward was checkpointed. A restart
            // between waves therefore owes one physical arena sweep even when the durable
            // pending set is empty.
            _reattachRewardSweepRequired = true;
            _exploration.Reset();
            _controller.ResumeBetweenWaves(BotMonotonicClock.Now);
            _canStartWaveAt = BotMonotonicClock.Now +
                TimeSpan.FromSeconds(ctx.Settings.SimulacrumMinWaveDelaySeconds);
            Diagnostics.EventLog.Emit(
                "simulacrum", "simulacrum.reattach-between-waves",
                Diagnostics.EventSeverity.Info,
                $"reattached after wave {wave}; recover rewards before next start",
                new Dictionary<string, object?> { ["wave"] = wave });
        }

        // Cached StateMachine values cannot end a wave. While the monolith is outside the
        // current entity traversal, continue safe combat/sweeping; once dry, return to its
        // retained anchor and obtain a fresh observation.
        if (_monolith.IsStale)
        {
            // Once an inactive edge was positively observed, the wave is over. The monolith
            // may scroll out of memory while we stand at the reward pile; do not abandon loot
            // merely to refresh a state that has already completed its job.
            if (_controller.Phase == SimulacrumPhase.Looting && _lastActiveKnown && !_lastActive)
            {
                TickLoot(ctx);
                LastDecision = $"Looting at reward anchor; monolith refresh not required ({_pendingRewardLoot.Count} pending)";
                return BehaviorStatus.Running;
            }
            var target = NearestTarget(ctx, ctx.Settings.ProximityEngageRadiusGrid);
            if (_sawActiveWave && (target is not null || !_exploration.IsExhausted))
            {
                TickFight(ctx);
                LastDecision = "Wave state stale: continuing safe combat/sweep";
            }
            else
            {
                _returnToMonolith.Tick(ctx);
                LastDecision = "Wave state stale: returning to cached monolith anchor";
            }
            return BehaviorStatus.Running;
        }

        var rewards = ObserveRewards(ctx);
        if (_controller.Phase == SimulacrumPhase.Looting
            && rewards.Truth == ObservationTruth.False
            && (_inventoryProbe != InventoryProbePhase.Idle
                || NeedsInventorySample(ctx.Settings, wave)))
        {
            TickInventoryProbe(ctx, wave);
            LastDecision = $"Looting/InventoryProbe: {_inventoryProbe} occupied={_inventoryOccupiedCells}";
            return BehaviorStatus.Running;
        }

        // Navigation from the reward anchor is outside the start-confirmation timeout.
        // Approach while still AwaitingWave, then let the controller enter StartingWave
        // only after the character is inside actual interaction range.
        var logicalStartReady = ObserveStartReady(active, wave);
        if (_controller.Phase == SimulacrumPhase.AwaitingWave
            && logicalStartReady.IsTrue
            && !NearMonolith(ctx, ctx.Settings.InteractionRangeGrid))
        {
            _returnToMonolith.Tick(ctx);
            LastDecision = "AwaitingWave/ApproachMonolith: moving into click radius before start timer";
            return BehaviorStatus.Running;
        }

        var frame = new SimulacrumFrame(
            BotMonotonicClock.Now,
            wave,
            _deathCount,
            logicalStartReady,
            active,
            ObserveComplete(active),
            rewards,
            InventoryNeedsDeposit(ctx.Settings, wave));
        var decision = _controller.Tick(frame);
        LastDecision = $"{decision.Phase}/{decision.Command}: {decision.Reason}";
        LogDecision(decision, wave);

        switch (decision.Command)
        {
            case SimulacrumCommand.StartWave:
                // Destructive boundary: starting a new wave deletes remaining ground drops.
                // Re-check the live snapshot even though ObserveRewards already gates this;
                // accepted loot must drain or the run stays here for diagnosis.
                TrackPendingRewardLoot(ctx);
                if (_pendingRewardLoot.Count > 0)
                {
                    LastDecision = $"StartWave LOCKED: {_pendingRewardLoot.Count} accepted reward item(s) remain";
                    TickLoot(ctx);
                }
                else
                {
                    _startWave.Tick(ctx);
                }
                break;
            case SimulacrumCommand.Fight:
                if (_stash.CurrentPhase != StashDepositSystem.Phase.Idle)
                    _stash.Cancel();
                if (ctx.Snapshot.IsStashOpen || ctx.Snapshot.Inventory.IsOpen)
                {
                    _movement.Release();
                    _combat.StopAllChannels();
                    ctx.Input.TapKey(VkEscape, ClickIntent.InteractUi,
                        "close between-wave panels for active Simulacrum wave");
                    break;
                }
                TickFight(ctx);
                break;
            case SimulacrumCommand.Loot:
                TickLoot(ctx);
                break;
            case SimulacrumCommand.Deposit:
                TickDeposit(ctx, wave);
                break;
            case SimulacrumCommand.Leave:
                _exit.Tick(ctx);
                break;
            case SimulacrumCommand.Stop:
            case SimulacrumCommand.Wait:
            default:
                _movement.Release();
                _combat.StopAllChannels();
                break;
        }
        return BehaviorStatus.Running;
    }

    private void UpdateLandmarks(BehaviorContext ctx)
    {
        _monolith = FindMonolith(ctx);
        if (_monolith is not null)
        {
            CaptureSaneLandmark(_monolith, ref _monolithId, ref _monolithAnchor);
            RunId ??= $"simulacrum-{ctx.Snapshot.AreaHash:X8}";
        }

        if (ctx.Entities is null) return;
        foreach (var entity in ctx.Entities.Entries.Values)
        {
            if (entity.Kind == EntityListReader.EntityKind.Stash)
                CaptureSaneLandmark(entity, ref _stashId, ref _stashAnchor);
            else if (entity.Kind == EntityListReader.EntityKind.TownPortal)
                CaptureSaneLandmark(entity, ref _portalId, ref _portalAnchor);
        }
    }

    private void ObserveActiveEdge(BooleanObservation active, int wave, BehaviorContext ctx)
    {
        if (!active.IsKnown) return;
        var nowActive = active.IsTrue;
        if (!_lastActiveKnown)
        {
            _lastActiveKnown = true;
            _lastActive = nowActive;
            if (nowActive)
            {
                // The monolith persists across all waves. Positive active evidence closes
                // this interaction cycle even though the controller no longer calls the
                // click behavior once it transitions to Fighting.
                _startWave.Reset();
                ClearRecovery(ctx.Snapshot.AreaHash, "attached during active wave");
                _sawActiveWave = true;
                _activeWave = wave;
                ResetPortalRefreshBudgetForWave(wave);
                _waveSweepPass = Math.Max(1, _waveSweepPass);
                _nextSweepResetAt = TimeSpan.MinValue;
                _exploration.Reset();
            }
            else
            {
                _activeWave = Math.Max(_activeWave, wave);
            }
            return;
        }

        if (nowActive && !_lastActive)
        {
            // Reset on the authoritative inactive->active edge, not inside the interaction
            // behavior: the lifecycle switches to Fight immediately and otherwise never
            // gives that behavior a tick in which to observe its own postcondition.
            _startWave.Reset();
            ClearRecovery(ctx.Snapshot.AreaHash, $"wave {_activeWave + 1} confirmed active");
            _sawActiveWave = true;
            _activeWave = SimulacrumController.CorrelateStartedWave(wave, _activeWave, MaxWaves);
            ResetPortalRefreshBudgetForWave(_activeWave, force: true);
            _waveSweepPass = 1;
            _nextSweepResetAt = TimeSpan.MinValue;
            _lootQuietSince = TimeSpan.MinValue;
            _lootActionableLastTick = false;
            _pendingRewardLoot.Clear();
            _rewardDropAnchor = null;
            _rewardSettleUntil = TimeSpan.MinValue;
            _exploration.Reset();
            _reattachRewardSweepRequired = false;
            _lootRecoverySweep = false;
            Diagnostics.EventLog.Emit("simulacrum", "simulacrum.wave-started",
                Diagnostics.EventSeverity.Info, $"wave {_activeWave} became active",
                new Dictionary<string, object?> { ["wave"] = _activeWave, ["observedWave"] = wave });
        }
        else if (nowActive && wave > _activeWave)
        {
            // The active flag can become visible one tick before the wave counter advances.
            // Keep the run correlation current so sweep/end telemetry and stash cadence do
            // not remain labelled as the previous wave.
            _activeWave = wave;
            ResetPortalRefreshBudgetForWave(wave, force: true);
        }
        else if (!nowActive && _lastActive)
        {
            _activeWave = Math.Max(_activeWave, wave);
            _canStartWaveAt = BotMonotonicClock.Now +
                TimeSpan.FromSeconds(ctx.Settings.SimulacrumMinWaveDelaySeconds);
            _rewardSettleUntil = BotMonotonicClock.Now +
                TimeSpan.FromSeconds(ctx.Settings.SimulacrumLootQuietSeconds);
            _rewardDropAnchor = ctx.Live?.GridPosition;
            _lootQuietSince = TimeSpan.MinValue;
            _lootActionableLastTick = false;
            _exploration.Reset();
            Diagnostics.EventLog.Emit("simulacrum", "simulacrum.wave-ended",
                Diagnostics.EventSeverity.Info, $"wave {_activeWave} became inactive",
                new Dictionary<string, object?> { ["wave"] = _activeWave });
            PersistRecovery(ctx, _activeWave);
        }
        _lastActive = nowActive;
    }

    private BooleanObservation ObserveStartReady(BooleanObservation active, int wave)
    {
        if (_monolith is null || !active.IsKnown || !_monolith.SimulacrumGoodbye.IsKnown)
            return Unknown("Simulacrum.startReady");
        var ready = !active.IsTrue
            && _monolith.SimulacrumGoodbye.Value == 0
            && wave < MaxWaves
            && BotMonotonicClock.Now >= _canStartWaveAt;
        return Known(ready, "Simulacrum.startReady");
    }

    private BooleanObservation ObserveComplete(BooleanObservation active)
    {
        if (!active.IsKnown || !_sawActiveWave) return Unknown("Simulacrum.waveComplete");
        return Known(!active.IsTrue, "Simulacrum.waveComplete");
    }

    private BooleanObservation ObserveRewards(BehaviorContext ctx)
    {
        if (_controller.Phase is not SimulacrumPhase.Looting)
            return Unknown("Simulacrum.rewards");
        TrackPendingRewardLoot(ctx);
        if (_pendingRewardLoot.Count > 0)
        {
            _lootQuietSince = TimeSpan.MinValue;
            return Known(true, "Simulacrum.rewards.pendingAcceptedLoot");
        }
        if (_lootActionableLastTick || _interact.IsBusy)
        {
            _lootQuietSince = TimeSpan.MinValue;
            return Known(true, "Simulacrum.rewards.actionable");
        }
        // The settle deadline starts on the authoritative active->inactive edge. Stay at the
        // reward anchor and continue scanning/clicking throughout this minimum window so
        // staggered drops are captured as they materialize.
        if (BotMonotonicClock.Now < _rewardSettleUntil)
            return Unknown("Simulacrum.rewards.dropSettleWindow");
        if (_reattachRewardSweepRequired)
        {
            if (!_exploration.IsExhausted)
                return Unknown("Simulacrum.rewards.reattachPhysicalSweep");
            _reattachRewardSweepRequired = false;
            PersistRecovery(ctx, ReadWave(_monolith));
        }
        return Known(false, "Simulacrum.rewards.exhausted");
    }

    private void TickFight(BehaviorContext ctx)
    {
        if (ctx.Live is null) return;
        _exploration.TrackVisit(ctx.Snapshot, ctx.Live.Value.GridPosition);
        // Global combat (shared with map farming): Righteous Fire emergency douse / re-light take
        // priority over engaging. Douse only on sustained low HP; re-light only when healthy and a
        // hostile is near. This is why Simulacrum keeps damage up after a death re-drops RF.
        if (_coord.ShouldDouseRequiredBuff(ctx)) { _coord.DouseRequiredBuffTick(ctx); return; }
        if (_coord.RequiredMapBuffMissing(ctx)) { _coord.EnsureRequiredMapBuffTick(ctx); return; }
        if (LowHp(ctx))
        {
            _movement.Halt(new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live));
            return;
        }

        // Route to the boss/rare FIRST — the nearest rare/unique, targetable or not — so we walk
        // in, mark it, and let trash swarm it (Penance Mark phantasms + Cast-on-X + RF). Only when
        // no rare+ is present do we fall back to the densest pack / nearest hostile. This is the
        // "go to the dense/rare monsters first" behaviour the CwS build needs on the boss wave.
        var target = _coord.SelectPriorityRare(ctx, ctx.Settings.ProximityEngageRadiusGrid)
                     ?? SelectCombatTarget(ctx, ctx.Settings.ProximityEngageRadiusGrid);
        // No target and the arena is swept dry: escalate (sweep again → portal refresh). This
        // repeats until the state changes or the controller's wave timeout fires.
        if (target is null && _exploration.IsExhausted)
        {
            if (_nextSweepResetAt == TimeSpan.MinValue)
                _nextSweepResetAt = BotMonotonicClock.Now + TimeSpan.FromSeconds(1);
            if (BotMonotonicClock.Now < _nextSweepResetAt)
            {
                _movement.Halt(new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live));
                return;
            }

            _nextSweepResetAt = TimeSpan.MinValue;
            if (_waveSweepPass >= DrySweepsBeforePortalRefresh)
            {
                ResetPortalRefreshBudgetForWave(_activeWave);
                if (!SimulacrumController.CanRefreshPortal(
                        _portalRefreshAttempts, MaxPortalRefreshAttemptsPerWave))
                {
                    _fatalReason = $"wave {_activeWave} remained active after "
                        + $"{_portalRefreshAttempts} portal refreshes and {_waveSweepPass} additional dry sweeps";
                    _movement.Halt(new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live));
                    Diagnostics.EventLog.Emit(
                        "simulacrum", "simulacrum.portal-refresh-exhausted",
                        Diagnostics.EventSeverity.Error, _fatalReason,
                        new Dictionary<string, object?>
                        {
                            ["wave"] = _activeWave,
                            ["attempts"] = _portalRefreshAttempts,
                            ["maxAttempts"] = MaxPortalRefreshAttemptsPerWave,
                            ["completedSweeps"] = _waveSweepPass,
                        });
                    return;
                }

                _portalRefreshAttempts++;
                _portalRefreshRequested = true;
                _movement.Halt(new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live));
                Diagnostics.EventLog.Emit(
                    "simulacrum", "simulacrum.portal-refresh-requested",
                    Diagnostics.EventSeverity.Warning,
                    $"wave {_activeWave} remained active after {_waveSweepPass} dry sweeps; requesting portal refresh",
                    new Dictionary<string, object?>
                    {
                        ["wave"] = _activeWave,
                        ["completedSweeps"] = _waveSweepPass,
                        ["attempt"] = _portalRefreshAttempts,
                        ["maxAttempts"] = MaxPortalRefreshAttemptsPerWave,
                    });
                return;
            }

            _exploration.Reset();
            _waveSweepPass++;
            Diagnostics.EventLog.Emit("simulacrum", "simulacrum.sweep-restarted",
                Diagnostics.EventSeverity.Info,
                $"wave {_activeWave} still active; starting sweep pass {_waveSweepPass}",
                new Dictionary<string, object?>
                {
                    ["wave"] = _activeWave,
                    ["pass"] = _waveSweepPass,
                });
        }
        else if (target is not null)
        {
            _nextSweepResetAt = TimeSpan.MinValue;
            // Target visibility must not refresh the deadline. This is a hard per-wave
            // budget so a permanently targetable, unkillable boss cannot hold the run open.
        }

        if (ctx.Settings.MapClearStance == 1)
        {
            if (target is not null && Distance(ctx.Live.Value.GridPosition, target.GridPosition)
                <= ctx.Settings.ProximityHoldRadiusGrid)
            {
                _movement.Halt(new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live));
                _coord.MarkTick(ctx);   // curse rare+ while standing in the pack (spawns phantasms)
                return;
            }
            _fightPath.Tick(ctx);
            _coord.MarkTick(ctx);       // keep marking while approaching the pack
            return;
        }

        var attack = PickAttackSlot(ctx.Settings);
        if (attack is not null && target is not null
            && Distance(ctx.Live.Value.GridPosition, target.GridPosition) <= attack.MaxRangeGrid
            && _skills.IsReady(attack))
        {
            _movement.Release();
            if (_combat.Cast(attack, Aim.AtEntity(target.Id), ctx, "simulacrum attack")
                == BehaviorStatus.Success)
                _skills.MarkCast(attack);
            _coord.MarkTick(ctx);
            return;
        }
        _fightPath.Tick(ctx);
        _coord.MarkTick(ctx);
    }

    private void ResetPortalRefreshBudgetForWave(int wave, bool force = false)
    {
        if (!force && _portalRefreshAttemptWave == wave) return;
        _portalRefreshAttemptWave = wave;
        _portalRefreshAttempts = 0;
    }

    private void TickLoot(BehaviorContext ctx)
    {
        if (ctx.Live is { } live)
            _exploration.TrackVisit(ctx.Snapshot, live.GridPosition);
        TrackPendingRewardLoot(ctx);
        _lootActionableLastTick = false;
        // Loot what is actionable at the current position before navigating anywhere. A wave
        // can end while the player is far from the monolith and the reward labels may already
        // be visible/in range there; forcing a return first made valid currency wait forever
        // behind a blocked or slow path to the monolith.
        var lootStatus = _loot.Tick(ctx);
        if (lootStatus == BehaviorStatus.Running || _interact.IsBusy)
        {
            _lootRecoverySweep = false;
            _lootActionableLastTick = true;
            _lootQuietSince = TimeSpan.MinValue;
            _movement.Release();
            return;
        }

        // No local click: path toward the nearest accepted drop remembered from a visible
        // label. Once that set drains, this same path returns to the monolith so the reward
        // quiet window and next-wave state can be observed safely.
        _lootSweepPath.Tick(ctx);
        if (_pendingRewardLoot.Count > 0
            && _lootSweepPath.LastDecision == "no path"
            && !_lootRecoverySweep)
        {
            _lootRecoverySweep = true;
            _exploration.Reset();
            Diagnostics.EventLog.Emit("simulacrum", "simulacrum.reward-path-recovery",
                Diagnostics.EventSeverity.Warning,
                "accepted reward position had no path; sweeping arena to reacquire its live label",
                new Dictionary<string, object?>
                {
                    ["pending"] = _pendingRewardLoot.Count,
                    ["wave"] = ReadWave(_monolith),
                });
        }
        else if (_lootRecoverySweep && _exploration.IsExhausted
                 && _pendingRewardLoot.Count > 0)
        {
            _fatalReason = $"accepted reward remains unreachable after full arena sweep: "
                + string.Join(", ", _pendingRewardLoot.Values.Select(x => x.Name));
        }
    }

    private void TrackPendingRewardLoot(BehaviorContext ctx)
    {
        if (_controller.Phase != SimulacrumPhase.Looting) return;
        var filter = LootClosestVisible.SharedValueFilter;
        var observed = new HashSet<uint>();
        long anchorX = 0, anchorY = 0;
        var anchorCount = 0;
        foreach (var label in ctx.Snapshot.GroundLabels)
        {
            if (!label.IsItem || label.EntityId == 0 || label.EntityGridPosition is not { } rawPosition)
                continue;
            observed.Add(label.EntityId);
            if (!label.IsLabelVisible) continue; // the user's loot filter is the primary allowlist
            if (filter is not null && !filter.Evaluate(label, ctx.Settings.Loot).ShouldTake) continue;
            var position = IsSaneRewardPosition(ctx, rawPosition)
                ? rawPosition
                : _rewardDropAnchor ?? ctx.Live?.GridPosition ?? rawPosition;
            _pendingRewardLoot[label.EntityId] = new PendingRewardLoot(position, label.ItemName);
            if (label.IsRectOnScreen || label.DistanceToPlayer <= ctx.Settings.LootRangeGrid)
                _lootRecoverySweep = false;
            anchorX += position.X;
            anchorY += position.Y;
            anchorCount++;
        }
        if (anchorCount > 0)
            _rewardDropAnchor = new Vector2i
            {
                X = (int)(anchorX / anchorCount),
                Y = (int)(anchorY / anchorCount),
            };

        // A confirmed pickup removes the world label. Only forget it when we are back at the
        // remembered position; absence while far away merely means it left the network bubble.
        if (ctx.Live is not { } live) return;
        List<uint>? removed = null;
        foreach (var (id, item) in _pendingRewardLoot)
        {
            if (observed.Contains(id)) continue;
            if (Distance(live.GridPosition, item.Position) > 18f) continue;
            (removed ??= new List<uint>()).Add(id);
        }
        if (removed is not null)
            foreach (var id in removed) _pendingRewardLoot.Remove(id);
        PersistRecovery(ctx, ReadWave(_monolith));
    }

    private Vector2i? NearestPendingReward(BehaviorContext ctx)
    {
        if (_pendingRewardLoot.Count == 0 || ctx.Live is not { } live) return null;
        return _pendingRewardLoot.Values
            .OrderBy(x => Distance(live.GridPosition, x.Position))
            .Select(x => (Vector2i?)x.Position)
            .FirstOrDefault();
    }

    private void TickDeposit(BehaviorContext ctx, int wave)
    {
        if (!_stash.IsBusy)
        {
            if (_stash.CurrentPhase == StashDepositSystem.Phase.Failed)
            {
                _fatalReason = $"between-wave stash failed: {_stash.Status}";
                return;
            }
            if (_stash.CurrentPhase == StashDepositSystem.Phase.Done)
            {
                // The shared map-farming flow closes the stash before moving on. Simulacrum
                // stays in the same area, so it must do that explicitly before the controller
                // is allowed to click the monolith for the next wave.
                if (ctx.Snapshot.IsStashOpen || ctx.Snapshot.Inventory.IsOpen)
                {
                    if (ctx.Snapshot.Inventory.IsOpen)
                        SetExactInventoryOccupancy(
                            ctx.Snapshot.Inventory.OccupiedCells,
                            $"wave {wave} post-deposit inventory");
                    ctx.Input.TapKey(VkEscape, ClickIntent.InteractUi,
                        "close Simulacrum stash after deposit");
                    return;
                }
                _depositedWaves.Add(wave);
                // Done belongs to the completed deposit transaction. Return the shared
                // subsystem to Idle after crediting this wave; otherwise the next threshold
                // crossing sees the old Done phase and falsely credits a deposit without
                // opening the stash or moving anything.
                _stash.Cancel();
                return;
            }
            _stash.Start(ctx.Settings.SimulacrumDumpTabName);
        }

        var result = _stash.Tick(ctx);
        if (result == StashDepositSystem.Result.Succeeded)
        {
            // Defer completion until the next tick verifies that Escape actually closed the
            // stash/inventory pair. That keeps the next world interaction panel-safe.
            if (ctx.Snapshot.IsStashOpen || ctx.Snapshot.Inventory.IsOpen)
            {
                if (ctx.Snapshot.Inventory.IsOpen)
                    SetExactInventoryOccupancy(
                        ctx.Snapshot.Inventory.OccupiedCells,
                        $"wave {wave} post-deposit inventory");
                ctx.Input.TapKey(VkEscape, ClickIntent.InteractUi,
                    "close Simulacrum stash after deposit");
            }
            else
            {
                _depositedWaves.Add(wave);
                _stash.Cancel();
            }
        }
        else if (result == StashDepositSystem.Result.Failed)
            _fatalReason = $"between-wave stash failed: {_stash.Status}";
    }

    private bool InventoryNeedsDeposit(BotSettings settings, int wave)
    {
        // Once the controller enters Depositing, the threshold has served its purpose as a
        // trigger. Keep the command latched until the adapter has positively completed the
        // entire stash flow, closed its panels, and credited this wave. Falling from 15 to
        // 14 cells after the first moved item is not "deposit complete."
        if (_controller.Phase == SimulacrumPhase.Depositing)
            return !_depositedWaves.Contains(wave);

        var threshold = settings.SimulacrumStashOccupiedCells;
        return threshold > 0 && wave > 0
            && _inventoryEstimateKnown
            && _inventoryOccupiedCells >= threshold
            && !_depositedWaves.Contains(wave);
    }

    private bool NeedsInventorySample(BotSettings settings, int wave)
        => settings.SimulacrumStashOccupiedCells > 0
            && wave > 0
            && !_inventoryEstimateKnown;

    private void TickInventoryProbe(BehaviorContext ctx, int wave)
    {
        _movement.Release();
        _combat.StopAllChannels();
        var now = BotMonotonicClock.Now;
        if (_inventoryProbeWave != wave)
        {
            _inventoryProbeWave = wave;
            _inventoryProbe = InventoryProbePhase.Idle;
            _inventoryProbeAt = now;
        }

        var inventory = ctx.Snapshot.Inventory;
        switch (_inventoryProbe)
        {
            case InventoryProbePhase.Idle:
                if (inventory.IsOpen)
                {
                    _inventoryProbe = InventoryProbePhase.Reading;
                    goto case InventoryProbePhase.Reading;
                }
                ctx.Input.TapKey(ctx.Settings.SimulacrumInventoryKeyVk,
                    ClickIntent.InteractUi, "open inventory for Simulacrum occupancy sample");
                _inventoryProbe = InventoryProbePhase.Opening;
                _inventoryProbeAt = now;
                return;

            case InventoryProbePhase.Opening:
                if (inventory.IsOpen)
                {
                    _inventoryProbe = InventoryProbePhase.Reading;
                    goto case InventoryProbePhase.Reading;
                }
                if ((now - _inventoryProbeAt).TotalSeconds <= 3) return;
                // If UI visibility cannot be read, take the safe operational fallback and
                // deposit rather than starting another wave with an unknown/full inventory.
                _inventoryOccupiedCells = ctx.Settings.SimulacrumStashOccupiedCells;
                _inventoryEstimateKnown = true;
                _inventoryEstimateExact = false;
                _inventoryProbe = InventoryProbePhase.Idle;
                Diagnostics.EventLog.Emit("simulacrum", "simulacrum.inventory-probe-fallback",
                    Diagnostics.EventSeverity.Warning,
                    "inventory did not open/read; conservatively requesting deposit",
                    new Dictionary<string, object?> { ["wave"] = wave });
                return;

            case InventoryProbePhase.Reading:
                SetExactInventoryOccupancy(inventory.OccupiedCells, $"wave {wave} probe");
                ctx.Input.TapKey(ctx.Settings.SimulacrumInventoryKeyVk,
                    ClickIntent.InteractUi, "close inventory after Simulacrum occupancy sample");
                _inventoryProbe = InventoryProbePhase.Closing;
                _inventoryProbeAt = now;
                return;

            case InventoryProbePhase.Closing:
                if (inventory.IsOpen && (now - _inventoryProbeAt).TotalSeconds <= 3) return;
                _inventoryProbe = InventoryProbePhase.Idle;
                Diagnostics.EventLog.Emit("simulacrum", "simulacrum.inventory-sampled",
                    Diagnostics.EventSeverity.Info,
                    $"wave {wave}: {_inventoryOccupiedCells}/60 inventory cells occupied",
                    new Dictionary<string, object?>
                    {
                        ["wave"] = wave,
                        ["occupiedCells"] = _inventoryOccupiedCells,
                        ["threshold"] = ctx.Settings.SimulacrumStashOccupiedCells,
                    });
                return;
        }
    }

    /// <summary>
    /// Seeded by the hideout orchestrator before entering an arena. Standalone runs fall back
    /// to a single probe only when no exact inventory observation has ever been available.
    /// </summary>
    public void SeedInventoryOccupancy(int occupiedCells)
        => SetExactInventoryOccupancy(occupiedCells, "hideout handoff");

    public void SeedDeathCount(int deaths)
        => _deathCount = Math.Max(0, deaths);

    private void OnPickupConfirmed(LootClosestVisible.ConfirmedPickup pickup)
    {
        _inventoryOccupiedCells = Math.Clamp(
            _inventoryOccupiedCells + Math.Max(1, pickup.OccupiedCells), 0, 60);
        // Stack merging can consume fewer cells than this upper bound. It is safe for capacity
        // decisions and is reconciled exactly whenever stash/inventory is naturally visible.
        _inventoryEstimateExact = false;
        _inventoryOccupancySource = _inventoryEstimateKnown
            ? "confirmed-pickup upper bound"
            : "unknown baseline plus confirmed pickups";
        Diagnostics.EventLog.Emit("simulacrum", "simulacrum.inventory-estimate-incremented",
            Diagnostics.EventSeverity.Debug,
            $"confirmed pickup +{pickup.OccupiedCells} cell(s); estimate {_inventoryOccupiedCells}/60",
            new Dictionary<string, object?>
            {
                ["name"] = pickup.Name,
                ["occupiedCellsAdded"] = pickup.OccupiedCells,
                ["occupiedCellsEstimate"] = _inventoryOccupiedCells,
                ["estimateKnown"] = _inventoryEstimateKnown,
            });
    }

    private void SetExactInventoryOccupancy(int occupiedCells, string source)
    {
        _inventoryOccupiedCells = Math.Clamp(occupiedCells, 0, 60);
        _inventoryEstimateKnown = true;
        _inventoryEstimateExact = true;
        _inventoryOccupancySource = source;
    }

    private Vector2i? FindFightGoal(BehaviorContext ctx)
    {
        if (_monolith is null && _monolithAnchor is not null)
            return _monolithAnchor;
        // Mirror TickFight's priority: route to the boss/rare (targetable or not) first, then the
        // densest pack / nearest, then the exploration frontier.
        var rare = _coord.SelectPriorityRare(ctx, ctx.Settings.ProximityEngageRadiusGrid);
        if (rare is not null) return rare.GridPosition;
        var target = SelectCombatTarget(ctx, ctx.Settings.ProximityEngageRadiusGrid);
        if (target is not null) return target.GridPosition;
        return _exploration.PickFrontier(ctx) ?? _monolithAnchor;
    }

    private EntityCache.Entry? SelectCombatTarget(BehaviorContext ctx, float radius)
    {
        if (ctx.Settings.MapClearStance != 1
            || ctx.Settings.ProximityDestinationPolicy == 0)
        {
            var nearest = NearestTarget(ctx, radius);
            _combatDestination = nearest is null
                ? "none"
                : $"nearest id={nearest.Id}";
            return nearest;
        }

        var selection = Threat.BestPack(
            ctx,
            radius,
            ctx.Settings.ProximityDensityRadiusGrid,
            entity => EnemyIgnoreList.IsIgnored(entity.Name));
        _combatDestination = selection is null
            ? "none"
            : $"pack id={selection.Value.Target.Id} rarity={Threat.RarityRank(selection.Value.Target)} "
              + $"nearby={selection.Value.NearbyCount} "
              + $"weight={selection.Value.DensityWeight:F0} score={selection.Value.Score:F1} "
              + $"distance={selection.Value.Distance:F1}";
        return selection?.Target;
    }

    private Vector2i? FindLootSweepGoal(BehaviorContext ctx)
    {
        if (_lootRecoverySweep)
            return _exploration.PickFrontier(ctx)
                ?? NearestPendingReward(ctx)
                ?? _rewardDropAnchor;
        var pending = NearestPendingReward(ctx);
        if (pending is not null) return pending;
        if (_reattachRewardSweepRequired)
            return _exploration.PickFrontier(ctx) ?? _rewardDropAnchor;
        return _rewardDropAnchor;
    }

    private void LoadRecovery(BehaviorContext ctx)
    {
        var areaHash = ctx.Snapshot.AreaHash;
        if (areaHash == 0
            || (_recoveryLoaded && _recoveryAreaHash == areaHash))
            return;

        _recoveryLoaded = true;
        _recoveryAreaHash = areaHash;
        _lastRecoverySignature = string.Empty;
        var state = _recovery.Load(areaHash);
        if (state is null)
        {
            _recoveryStatus = "no checkpoint";
            return;
        }

        var observedWave = ReadWave(_monolith);
        if (_monolith is not null && IsWaveActive(_monolith))
        {
            ClearRecovery(areaHash, "ignored checkpoint because wave is active");
            return;
        }
        if (observedWave > 0 && state.Wave != observedWave)
        {
            ClearRecovery(areaHash,
                $"discarded wave-mismatched checkpoint {state.Wave}!={observedWave}");
            return;
        }

        _monolithAnchor ??= ToVector(state.Monolith);
        _stashAnchor ??= ToVector(state.Stash);
        _portalAnchor ??= ToVector(state.Portal);
        _rewardDropAnchor ??= ToVector(state.RewardAnchor);
        foreach (var item in state.PendingItems ?? [])
        {
            if (item.Id == 0 || item.X == 0 && item.Y == 0) continue;
            _pendingRewardLoot[item.Id] = new PendingRewardLoot(
                new Vector2i { X = item.X, Y = item.Y }, item.Name);
        }
        _recoveryStatus = $"loaded wave {state.Wave}: {_pendingRewardLoot.Count} pending";
        Diagnostics.EventLog.Emit("simulacrum", "simulacrum.recovery-loaded",
            Diagnostics.EventSeverity.Warning, _recoveryStatus,
            new Dictionary<string, object?>
            {
                ["areaHash"] = $"0x{areaHash:X8}",
                ["wave"] = state.Wave,
                ["pending"] = _pendingRewardLoot.Count,
            });
    }

    private void PersistRecovery(BehaviorContext ctx, int wave)
    {
        var areaHash = ctx.Snapshot.AreaHash;
        if (areaHash == 0 || wave <= 0) return;
        var items = _pendingRewardLoot
            .OrderBy(x => x.Key)
            .Select(x => new SimulacrumRecoveryItem(
                x.Key, x.Value.Name, x.Value.Position.X, x.Value.Position.Y))
            .ToArray();
        var signature = $"{areaHash}:{wave}:{PointSignature(_monolithAnchor)}:"
            + $"{PointSignature(_stashAnchor)}:{PointSignature(_portalAnchor)}:"
            + $"{PointSignature(_rewardDropAnchor)}:"
            + string.Join('|', items.Select(x => $"{x.Id}:{x.X}:{x.Y}:{x.Name}"));
        if (signature == _lastRecoverySignature) return;

        try
        {
            _recovery.Save(new SimulacrumRecoveryState(
                areaHash,
                wave,
                ToRecoveryPoint(_monolithAnchor),
                ToRecoveryPoint(_stashAnchor),
                ToRecoveryPoint(_portalAnchor),
                ToRecoveryPoint(_rewardDropAnchor),
                items));
            _lastRecoverySignature = signature;
            _recoveryStatus = $"saved wave {wave}: {items.Length} pending";
        }
        catch (Exception ex)
        {
            _recoveryStatus = $"checkpoint save failed: {ex.Message}";
            Diagnostics.EventLog.Emit("simulacrum", "simulacrum.recovery-save-failed",
                Diagnostics.EventSeverity.Error, _recoveryStatus);
        }
    }

    private void ClearRecovery(uint areaHash, string reason)
    {
        if (areaHash != 0) _recovery.Delete(areaHash);
        _lastRecoverySignature = string.Empty;
        _recoveryStatus = reason;
    }

    private bool IsSaneRewardPosition(BehaviorContext ctx, Vector2i position)
    {
        if (position.X == 0 && position.Y == 0) return false;
        var reference = _monolithAnchor ?? ctx.Live?.GridPosition;
        return reference is null || Distance(reference.Value, position) <= 300f;
    }

    private static SimulacrumRecoveryPoint? ToRecoveryPoint(Vector2i? point)
        => point is { } p ? new SimulacrumRecoveryPoint(p.X, p.Y) : null;

    private static Vector2i? ToVector(SimulacrumRecoveryPoint? point)
        => point is { } p ? new Vector2i { X = p.X, Y = p.Y } : null;

    private static string PointSignature(Vector2i? point)
        => point is { } p ? $"{p.X},{p.Y}" : "-";

    private EntityCache.Entry? FindMonolith(BehaviorContext ctx)
    {
        if (ctx.Entities is null) return null;
        if (_monolithId != 0
            && ctx.Entities.Entries.TryGetValue(_monolithId, out var existing)
            && existing.Path.Contains("Objects/Afflictionator", StringComparison.OrdinalIgnoreCase))
            return existing;
        return ctx.Entities.Entries.Values.FirstOrDefault(entity =>
            entity.Path.Contains("Objects/Afflictionator", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsWaveActive(EntityCache.Entry entity)
        => !entity.IsStale && entity.SimulacrumActive.IsKnown && entity.SimulacrumGoodbye.IsKnown
        && entity.SimulacrumActive.Value > 0 && entity.SimulacrumGoodbye.Value == 0;

    private static BooleanObservation ObserveActive(EntityCache.Entry? entity)
    {
        if (entity is null || entity.IsStale
            || !entity.SimulacrumActive.IsKnown || !entity.SimulacrumGoodbye.IsKnown)
            return Unknown("Simulacrum.waveActive");
        return Known(IsWaveActive(entity), "Simulacrum.waveActive");
    }

    private static void CaptureSaneLandmark(
        EntityCache.Entry entity, ref uint id, ref Vector2i? anchor)
    {
        var position = entity.GridPosition;
        if (position.X == 0 && position.Y == 0) return;
        // Entity IDs can be re-resolved after streaming. Accept the first valid position and
        // later updates only when they stay within AutoExile's proven 50-grid sanity window.
        if (anchor is { } prior && Distance(prior, position) > 50f) return;
        id = entity.Id;
        anchor = position;
    }

    private static int ReadWave(EntityCache.Entry? entity)
        => entity?.SimulacrumWave.IsKnown == true
            ? (int)Math.Clamp(entity.SimulacrumWave.Value, 0, MaxWaves)
            : 0;

    private static EntityCache.Entry? NearestTarget(BehaviorContext ctx, float radius)
    {
        if (ctx.Entities is null || ctx.Live is null) return null;
        var player = ctx.Live.Value.GridPosition;
        EntityCache.Entry? best = null;
        var bestD2 = radius * radius;
        foreach (var entity in ctx.Entities.Entries.Values)
        {
            if (!TargetEligibility.IsEligible(entity) || EnemyIgnoreList.IsIgnored(entity.Name)) continue;
            var dx = (float)(entity.GridPosition.X - player.X);
            var dy = (float)(entity.GridPosition.Y - player.Y);
            var d2 = dx * dx + dy * dy;
            if (d2 < bestD2) { bestD2 = d2; best = entity; }
        }
        return best;
    }

    private bool NearMonolith(BehaviorContext ctx, float radius)
        => _monolithAnchor is { } anchor && ctx.Live is { } live
        && Distance(anchor, live.GridPosition) <= radius;

    private static SkillSlot? PickAttackSlot(BotSettings settings)
        => settings.Skills.Slots.FirstOrDefault(slot => slot.Role == SkillRole.Attack && slot.Vk != 0);

    private static bool LowHp(BehaviorContext ctx)
    {
        if (ctx.Live is not { } live || live.HpMax <= 0 || ctx.Settings.HpRetreatThreshold <= 0)
            return false;
        return (float)live.HpCurrent / live.HpMax < ctx.Settings.HpRetreatThreshold;
    }

    private void LogDecision(SimulacrumDecision decision, int wave)
    {
        if (decision.Phase == _lastLoggedPhase && decision.Command == _lastLoggedCommand) return;
        _lastLoggedPhase = decision.Phase;
        _lastLoggedCommand = decision.Command;
        Diagnostics.EventLog.Emit("simulacrum", "simulacrum.decision",
            Diagnostics.EventSeverity.Info, LastDecision,
            new Dictionary<string, object?>
            {
                ["phase"] = decision.Phase.ToString(),
                ["command"] = decision.Command.ToString(),
                ["reason"] = decision.Reason,
                ["wave"] = wave,
            });
    }

    private static SimulacrumController CreateController(BotSettings settings)
        => new(MaxWaves, settings.SimulacrumMaxDeaths,
            TimeSpan.FromSeconds(settings.SimulacrumStartTimeoutSeconds),
            TimeSpan.FromSeconds(settings.SimulacrumWaveTimeoutSeconds));

    private static BooleanObservation Known(bool value, string source)
        => BooleanObservation.Known(value, source, 0, ObservationConfidence.Validated);

    private static BooleanObservation Unknown(string source)
        => BooleanObservation.Unknown(source, 0, ObservationReadStatus.InvalidValue);

    private static float Distance(Vector2i a, Vector2i b)
    {
        var dx = (float)(a.X - b.X);
        var dy = (float)(a.Y - b.Y);
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
