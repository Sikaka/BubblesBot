using BubblesBot.Bot.Behaviors;
using BubblesBot.Bot.Behaviors.Interact;
using BubblesBot.Bot.Behaviors.Movement;
using BubblesBot.Bot.Input;
using BubblesBot.Bot.Settings;
using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Modes;

/// <summary>
/// Cross-area Simulacrum farming lifecycle: withdraw one map item from the currently
/// visible Delirium stash tab, right-click one into the map device, enter fresh portals,
/// run the arena controller, exit, and repeat until limits or supplies stop the session.
/// </summary>
public sealed class SimulacrumRunMode : IBotMode
{
    private enum Step { Boot, Supply, Device, Arena, Recover, Stopped }
    private enum RecoveryLeg { ExitArena, AwaitSafeHub, ReturnToArena }

    private const int VkEscape = 0x1B;
    private const int VkLeftControl = 0xA2;
    private const int MaxSupplyClicks = 4;
    private static readonly TimeSpan ArenaDepartureConfirmation = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan UnknownAreaHydrationWindow = TimeSpan.FromMilliseconds(1500);

    private readonly SettingsStore _settings;
    private readonly Func<GameSnapshot?> _getSnapshot;
    private readonly Func<LivePlayer?> _getLive;
    private readonly Func<EntityCache?> _getEntities;
    private readonly MovementSystem _movement;
    private readonly SkillBook _skills;
    private readonly InteractSystem _interact = new();
    private readonly InteractWorldEntity _openStash;
    private readonly EnterAreaTransition _returnThroughPortal;
    private readonly StashTabSwitcher _supplyTabSwitcher;
    private readonly MapDeviceSystem _device;
    private readonly SimulacrumMode _arena;
    private readonly ExplorationSystem _recoveryExploration = new(25);
    private readonly FollowPath _recoveryExplore;
    private readonly AreaTransitionTracker _entryTransition = new(TimeSpan.FromSeconds(12));
    private readonly AreaTransitionTracker _exitTransition = new(TimeSpan.FromSeconds(12));
    private readonly SimulacrumRecoveryStore _bootRecovery = new();

    private Step _step = Step.Boot;
    private uint _arenaAreaHash;
    private int _runsStarted;
    private int _runsCompleted;
    private int _runsAbandoned;
    private TimeSpan _sessionStartedAt = BotMonotonicClock.Now;
    private int _lastKnownInventoryCells;
    private bool _lastKnownInventoryCellsKnown;
    private int _carriedSupplies;
    private bool _supplyCommittedToDevice;
    private int _supplyClickAttempts;
    private TimeSpan _lastSupplyActionAt = TimeSpan.MinValue;
    private TimeSpan _supplyMissingSince = TimeSpan.MinValue;
    private TimeSpan _supplyPanelObservedAt = TimeSpan.MinValue;
    private TimeSpan _arenaMismatchSince = TimeSpan.MinValue;
    private TimeSpan _bootUnknownSince = TimeSpan.MinValue;
    private TimeSpan _recoveryStartedAt = TimeSpan.MinValue;
    private RecoveryLeg _recoveryLeg = RecoveryLeg.ReturnToArena;
    // Return-leg guard: true once the re-entry portal was clicked, so we wait for the arena area
    // to actually load before latching it (a premature EnterAreaTransition success once latched
    // the hideout hash and hung the run — 2026-07-16 wave-11 incident).
    private bool _reentryPortalEntered;
    private string _recoveryReason = string.Empty;
    private uint _recoveryOriginAreaHash;
    private int _runDeaths;
    private int _waveDeaths;
    private int _deathBudgetWave;
    private bool _deathRecoveryPending;
    private bool _discardExistingRun;
    private bool _abandonCurrentRun;
    private string _supplyTarget = "none";
    private bool _stopped;
    private string _stopReason = string.Empty;
    private string _runId = Guid.NewGuid().ToString("N");

    public string Name => "Simulacrum farming";
    public IBehavior Root => _arena.Root;
    public string LastDecision { get; private set; } = "init";
    public string RunId => _runId;
    public void RestartActiveWaveClock() => _arena.RestartActiveWaveClock();
    public IReadOnlyList<string> HudLines
    {
        get
        {
            var target = _settings.Current.SimulacrumTargetRuns;
            var elapsed = _arena.ActiveWaveElapsed;
            var limit = _arena.ActiveWaveLimit;
            var running = BotMonotonicClock.Now - _sessionStartedAt;
            return
            [
                $"Simulacrum: {_step} | Runs: {_runsCompleted}/{(target > 0 ? target.ToString() : "∞")}",
                $"Attempts: {_runsStarted} | Abandoned: {_runsAbandoned} | Running: {running:hh\\:mm\\:ss}",
                $"Wave: {_arena.ActiveWave}/15 | {_arena.Phase} | {elapsed.TotalSeconds:F0}/{limit.TotalSeconds:F0}s",
                $"Deaths: {_waveDeaths}/{_settings.Current.SimulacrumMaxDeaths} this wave | {_runDeaths} run",
                LastDecision,
            ];
        }
    }
    public object Telemetry => new
    {
        lifecycleStep = _step.ToString(),
        runsStarted = _runsStarted,
        runsCompleted = _runsCompleted,
        runsAbandoned = _runsAbandoned,
        runningSeconds = Math.Max(0, (BotMonotonicClock.Now - _sessionStartedAt).TotalSeconds),
        targetRuns = _settings.Current.SimulacrumTargetRuns,
        carriedSupplies = _carriedSupplies,
        lastKnownInventoryCells = _lastKnownInventoryCells,
        devicePhase = _device.CurrentPhase.ToString(),
        deviceStatus = _device.Status,
        entryTransition = _entryTransition.State,
        exitTransition = _exitTransition.State,
        stopped = _stopped,
        stopReason = _stopReason,
        runDeaths = _runDeaths,
        waveDeaths = _waveDeaths,
        deathBudgetWave = _deathBudgetWave,
        maxDeathsPerWave = _settings.Current.SimulacrumMaxDeaths,
        deathRecoveryPending = _deathRecoveryPending,
        abandonCurrentRun = _abandonCurrentRun,
        discardExistingRun = _discardExistingRun || _settings.Current.SimulacrumDiscardExistingRun,
        recoveryLeg = _step == Step.Recover ? _recoveryLeg.ToString() : "none",
        recoveryReason = _recoveryReason,
        recoveryOriginAreaHash = _recoveryOriginAreaHash,
        supplyTarget = _supplyTarget,
        phase = _arena.Phase.ToString(),
        arena = _arena.Telemetry,
    };

    public SimulacrumRunMode(
        SettingsStore settings,
        CombatCoordinator coord,
        Func<GameSnapshot?> getSnapshot,
        Func<LivePlayer?> getLive,
        Func<EntityCache?> getEntities)
    {
        _settings = settings;
        _getSnapshot = getSnapshot;
        _getLive = getLive;
        _getEntities = getEntities;
        // Share the one combat authority (movement/skills) with the arena combat brain.
        _movement = coord.Movement;
        _skills = coord.Skills;
        _arena = new SimulacrumMode(settings, coord, getSnapshot, getLive, getEntities);
        _device = new MapDeviceSystem(_movement, _skills, getSnapshot, getLive, getEntities);
        _supplyTabSwitcher = new StashTabSwitcher(getSnapshot);
        _openStash = new InteractWorldEntity(
            "open supply stash", _interact, _movement, _skills,
            FindStash,
            (ctx, _) => ctx.Snapshot.IsStashOpen);
        _returnThroughPortal = new EnterAreaTransition(
            "return to Simulacrum after death", _interact, _movement, _skills,
            getSnapshot,
            entity => entity.Kind == EntityListReader.EntityKind.TownPortal
                   || entity.Kind == EntityListReader.EntityKind.Portal,
            _ => _recoveryLeg == RecoveryLeg.ExitArena ? _arena.PortalAnchor : null);
        _recoveryExplore = new FollowPath(
            "simulacrum recovery portal search", _movement,
            ctx => _recoveryExploration.PickFrontier(ctx), _skills,
            goalArrivalRadius: 6f);
    }

    public void Tick(GameSnapshot snapshot, IInputRouter input)
    {
        if (snapshot.Player is { } player)
            _skills.SetActorContext(player.ActorComponentAddress);
        if (_skills.CooldownReader is null)
            _skills.CooldownReader = new SkillCooldownReader(snapshot.Reader);

        if (_stopped)
        {
            Reset();
            return;
        }

        var ctx = new BehaviorContext(
            snapshot, input, _settings.Current, _getLive(), _getEntities());
        if (snapshot.Inventory.IsOpen)
        {
            _lastKnownInventoryCells = snapshot.Inventory.OccupiedCells;
            _lastKnownInventoryCellsKnown = true;
            _carriedSupplies = CountCarriedSupplies(snapshot.Inventory);
        }

        switch (_step)
        {
            case Step.Boot: TickBoot(ctx); break;
            case Step.Supply: TickSupply(ctx); break;
            case Step.Device: TickDevice(ctx); break;
            case Step.Arena: TickArena(snapshot, input, ctx); break;
            case Step.Recover: TickRecovery(ctx); break;
            case Step.Stopped: break;
        }
    }

    /// <summary>
    /// Called by the global death gate on the confirmed checkpoint-revive edge. Returning
    /// true means this run owns a validated resume-through-existing-portal recovery and the
    /// caller should keep automation armed; false preserves the global fail-safe disarm.
    /// </summary>
    public bool NotifyRevived()
    {
        if (_step != Step.Arena || _arena.Phase == SimulacrumPhase.Terminal)
            return false;
        SyncWaveDeathBudget();
        _runDeaths++;
        _waveDeaths++;
        _deathRecoveryPending = true;
        _arena.SeedDeathCount(_waveDeaths);
        _abandonCurrentRun = SimulacrumController.DeathLimitReached(
            _waveDeaths, _settings.Current.SimulacrumMaxDeaths);
        LastDecision = _abandonCurrentRun
            ? $"Arena/Death: wave {_deathBudgetWave} death limit reached "
                + $"({_waveDeaths}/{_settings.Current.SimulacrumMaxDeaths}); abandon instance"
            : $"Arena/Death: wave {_deathBudgetWave} death {_waveDeaths}; awaiting existing portal";
        if (_abandonCurrentRun)
        {
            // The confirmed checkpoint revive is already carrying us out of the arena.
            // Wait for safe-hub evidence, then consume a fresh supply; never re-enter this wave.
            _step = Step.Recover;
            _recoveryLeg = RecoveryLeg.AwaitSafeHub;
            _recoveryStartedAt = AreaTransitionTracker.MonotonicNow();
            _recoveryOriginAreaHash = _arenaAreaHash;
            _recoveryReason = LastDecision;
        }
        Diagnostics.EventLog.Emit(
            "simulacrum", "simulacrum.death-recovery-pending",
            Diagnostics.EventSeverity.Warning, LastDecision,
            new Dictionary<string, object?>
            {
                ["runDeaths"] = _runDeaths,
                ["waveDeaths"] = _waveDeaths,
                ["wave"] = _deathBudgetWave,
                ["maxDeathsPerWave"] = _settings.Current.SimulacrumMaxDeaths,
                ["abandon"] = _abandonCurrentRun,
            });
        // Even at the limit this mode owns the safe-hub continuation: it abandons the failed
        // instance and starts another supplied Simulacrum instead of re-entering this wave.
        return true;
    }

    public static bool ShouldResumeExistingPortal(
        bool discardExistingRun, bool existingPortalPresent, bool verifiedRecovery)
        => !discardExistingRun && existingPortalPresent && verifiedRecovery;

    public static bool ShouldAttachFreshUnknownMap(AreaTransitionState transition)
        => transition.ExpectedDestination == AreaRole.Map
        && transition.ObservedAreaHash != 0
        && transition.ObservedAreaHash != transition.OriginAreaHash
        && transition.ObservedRole == AreaRole.Unknown
        && transition.Outcome == AreaTransitionOutcome.VerifyingDestination;

    public void Reset()
    {
        _movement.Release();
        _interact.Cancel();
        _openStash.Reset();
        _supplyTabSwitcher.Reset();
        _returnThroughPortal.Reset();
        _recoveryExploration.Reset();
        _recoveryExplore.Reset();
        _device.Cancel();
        _arena.Reset();
        _entryTransition.Reset();
        _exitTransition.Reset();
        _step = Step.Boot;
        _arenaAreaHash = 0;
        _runsStarted = 0;
        _runsCompleted = 0;
        _runsAbandoned = 0;
        _sessionStartedAt = BotMonotonicClock.Now;
        _lastKnownInventoryCells = 0;
        _lastKnownInventoryCellsKnown = false;
        _carriedSupplies = 0;
        _supplyCommittedToDevice = false;
        _supplyClickAttempts = 0;
        _lastSupplyActionAt = TimeSpan.MinValue;
        _supplyMissingSince = TimeSpan.MinValue;
        _supplyPanelObservedAt = TimeSpan.MinValue;
        _arenaMismatchSince = TimeSpan.MinValue;
        _bootUnknownSince = TimeSpan.MinValue;
        _recoveryStartedAt = TimeSpan.MinValue;
        _recoveryLeg = RecoveryLeg.ReturnToArena;
        _reentryPortalEntered = false;
        _recoveryReason = string.Empty;
        _recoveryOriginAreaHash = 0;
        _runDeaths = 0;
        _waveDeaths = 0;
        _deathBudgetWave = 0;
        _deathRecoveryPending = false;
        _discardExistingRun = false;
        _abandonCurrentRun = false;
        _supplyTarget = "none";
        _stopped = false;
        _stopReason = string.Empty;
        _runId = Guid.NewGuid().ToString("N");
        LastDecision = "reset";
    }

    private void TickBoot(BehaviorContext ctx)
    {
        var monolith = FindMonolith(ctx);
        var role = WorldAreaClassifier.Classify(ctx);
        var hasRecovery = ctx.Snapshot.AreaHash != 0
            && _bootRecovery.Load(ctx.Snapshot.AreaHash) is not null;
        if (ctx.Settings.SimulacrumDiscardExistingRun
            && (monolith is not null || role == AreaRole.Map))
        {
            _discardExistingRun = true;
            _step = Step.Recover;
            _recoveryStartedAt = AreaTransitionTracker.MonotonicNow();
            _recoveryLeg = RecoveryLeg.ExitArena;
            _recoveryReason = "one-shot discard of existing Simulacrum";
            _recoveryOriginAreaHash = ctx.Snapshot.AreaHash;
            _returnThroughPortal.Reset();
            LastDecision = "Boot/Discard: exiting existing arena before fresh supply run";
            Diagnostics.EventLog.Emit(
                "simulacrum", "simulacrum.discard-started",
                Diagnostics.EventSeverity.Warning, LastDecision,
                new Dictionary<string, object?>
                {
                    ["areaHash"] = $"0x{ctx.Snapshot.AreaHash:X8}",
                });
            return;
        }
        if (monolith is not null || hasRecovery)
        {
            StartArena(ctx.Snapshot.AreaHash);
            LastDecision = monolith is not null
                ? "Boot/Arena: attached from visible Simulacrum monolith"
                : "Boot/Arena: attached from verified Simulacrum recovery checkpoint";
            return;
        }
        if (role == AreaRole.Map)
        {
            // Mode 6 alone cannot prove an arbitrary combat area is a Simulacrum. This is
            // especially important in hideouts with stale Blight/map portals: never wander
            // another map looking for an Afflictionator that cannot exist there.
            _movement.Release();
            LastDecision = "Boot: unverified combat area; waiting for Simulacrum monolith or explicit discard";
            return;
        }
        if (role == AreaRole.Unknown && ctx.Snapshot.AreaHash != 0)
        {
            // Unknown is not positive arena evidence. Wait for a monolith, a durable recovery
            // checkpoint, or a normal area classification instead of attaching by mode intent.
            var now = AreaTransitionTracker.MonotonicNow();
            if (_bootUnknownSince == TimeSpan.MinValue)
                _bootUnknownSince = now;
            if (now - _bootUnknownSince >= UnknownAreaHydrationWindow)
            {
                if (ctx.Settings.SimulacrumDiscardExistingRun)
                {
                    _discardExistingRun = true;
                    _step = Step.Recover;
                    _recoveryStartedAt = now;
                    _recoveryLeg = RecoveryLeg.ExitArena;
                    _recoveryReason = "one-shot discard of stable unknown Simulacrum entrance";
                    _recoveryOriginAreaHash = ctx.Snapshot.AreaHash;
                    _returnThroughPortal.Reset();
                    LastDecision = "Boot/Discard: exiting stable unknown arena before fresh supply run";
                    Diagnostics.EventLog.Emit(
                        "simulacrum", "simulacrum.discard-started",
                        Diagnostics.EventSeverity.Warning, LastDecision,
                        new Dictionary<string, object?>
                        {
                            ["areaHash"] = $"0x{ctx.Snapshot.AreaHash:X8}",
                            ["evidence"] = "stable unknown area in explicit Simulacrum mode",
                        });
                    return;
                }
                _movement.Release();
                LastDecision = "Boot: stable unknown area remains unverified; waiting safely";
                return;
            }
            LastDecision = "Boot: allowing destination entities to hydrate";
            return;
        }
        _bootUnknownSince = TimeSpan.MinValue;
        if (role == AreaRole.SafeHub || ctx.Snapshot.IsStashOpen)
        {
            var existingPortal = FindExistingPortal(ctx) is not null;
            if (ShouldResumeExistingPortal(
                    ctx.Settings.SimulacrumDiscardExistingRun, existingPortal,
                    verifiedRecovery: false))
            {
                _step = Step.Recover;
                _recoveryLeg = RecoveryLeg.ReturnToArena;
                _recoveryStartedAt = AreaTransitionTracker.MonotonicNow();
                _recoveryOriginAreaHash = ctx.Snapshot.AreaHash;
                _recoveryReason = "boot resume through existing portal";
                _returnThroughPortal.Reset();
                LastDecision = "Boot/Recovery: existing portal takes precedence over consuming supply";
                Diagnostics.EventLog.Emit(
                    "simulacrum", "simulacrum.boot-portal-resume",
                    Diagnostics.EventSeverity.Info, LastDecision,
                    new Dictionary<string, object?>
                    {
                        ["areaHash"] = $"0x{ctx.Snapshot.AreaHash:X8}",
                    });
                return;
            }
            if (ctx.Settings.SimulacrumDiscardExistingRun)
            {
                _settings.Mutate(settings => settings.SimulacrumDiscardExistingRun = false);
                Diagnostics.EventLog.Emit(
                    "simulacrum", "simulacrum.discard-hideout-confirmed",
                    Diagnostics.EventSeverity.Info,
                    "fresh-start request began in hideout; ignoring existing portal set");
            }
            _step = Step.Supply;
            LastDecision = ctx.Settings.SimulacrumDiscardExistingRun
                ? "Boot/Supply: discard request bypassed existing portals"
                : "Boot/Supply: hideout ready";
            return;
        }
        LastDecision = "Boot: waiting for hideout or Simulacrum evidence";
    }

    private void TickSupply(BehaviorContext ctx)
    {
        if (ctx.Snapshot.AtlasPanel.IsVisible)
        {
            ctx.Input.VerifiedTapKey(
                VkEscape, ClickIntent.InteractUi, "close leftover map-device panel before supply",
                expectResolved: () => !(_getSnapshot()?.AtlasPanel.IsVisible ?? true),
                timeoutMs: 1500);
            LastDecision = "Supply: closing leftover map-device panel";
            return;
        }
        var carried = _carriedSupplies;
        if (carried > 0)
        {
            _supplyMissingSince = TimeSpan.MinValue;
            if (ctx.Snapshot.IsStashOpen)
            {
                ctx.Input.VerifiedTapKey(
                    VkEscape, ClickIntent.InteractUi, "close supply stash",
                    expectResolved: () => !(_getSnapshot()?.IsStashOpen ?? true),
                    timeoutMs: 2000);
                LastDecision = $"Supply: carrying {carried}; closing stash";
                return;
            }

            _device.Start(
                ctx.Entities, MapDeviceSystem.PayloadSource.InventorySimulacrum);
            _supplyCommittedToDevice = false;
            _supplyClickAttempts = 0;
            _entryTransition.Reset();
            _step = Step.Device;
            LastDecision = $"Supply/Device: carrying {carried} Simulacrum(s)";
            return;
        }

        if (!ctx.Snapshot.IsStashOpen)
        {
            _supplyPanelObservedAt = TimeSpan.MinValue;
            _openStash.Tick(ctx);
            LastDecision = $"Supply/OpenStash: {_openStash.LastDecision}";
            return;
        }

        var stash = ctx.Snapshot.StashInventory;
        var supplyTabName = ctx.Settings.SimulacrumSupplyTabName.Trim();
        if (supplyTabName.Length > 0)
        {
            var targetTab = ctx.Snapshot.StashTabs.Find(
                supplyTabName, requireGeneralPurpose: false);
            if (targetTab is null)
            {
                Stop($"supply stash tab '{supplyTabName}' not found");
                return;
            }
            if (ctx.Snapshot.StashTabs.FindSelected(
                    supplyTabName, requireGeneralPurpose: false, stash.VisibleTabIndex) is null)
            {
                if (!_supplyTabSwitcher.IsStarted
                    || !_supplyTabSwitcher.TargetName.Equals(
                        supplyTabName, StringComparison.OrdinalIgnoreCase))
                    _supplyTabSwitcher.Start(supplyTabName, requireGeneralPurpose: false);
                var switchResult = _supplyTabSwitcher.Tick(ctx);
                LastDecision = $"Supply/SwitchTab: {_supplyTabSwitcher.Status}";
                if (switchResult == StashTabSwitcher.Result.Failed)
                    Stop($"supply-tab switch failed: {_supplyTabSwitcher.Status}");
                return;
            }
            if (_supplyTabSwitcher.IsStarted)
            {
                _supplyTabSwitcher.Reset();
                _supplyPanelObservedAt = BotMonotonicClock.Now;
                LastDecision = "Supply: Deli tab selected; settling specialized layout";
                return;
            }
        }
        if (_supplyPanelObservedAt == TimeSpan.MinValue)
        {
            _supplyPanelObservedAt = BotMonotonicClock.Now;
            LastDecision = "Supply: stash visible; settling specialized tab layout";
            return;
        }
        if (BotMonotonicClock.ElapsedSince(_supplyPanelObservedAt).TotalMilliseconds < 1500)
        {
            LastDecision = "Supply: waiting for specialized stash layout to settle";
            return;
        }
        var target = stash.Items.FirstOrDefault(item => item.Path.Contains(
            InventoryView.SimulacrumPathFragment, StringComparison.OrdinalIgnoreCase));
        if (target.ItemEntity == 0 || target.Rect is null)
        {
            _supplyMissingSince = _supplyMissingSince == TimeSpan.MinValue
                ? BotMonotonicClock.Now
                : _supplyMissingSince;
            if (BotMonotonicClock.ElapsedSince(_supplyMissingSince).TotalSeconds >= 2)
                Stop($"no Simulacrum stack on visible stash tab {stash.VisibleTabIndex}; "
                    + "select the Deli tab or restock supplies");
            else
                LastDecision = "Supply: waiting for visible Deli-tab Simulacrum stack";
            return;
        }

        if (_supplyClickAttempts >= MaxSupplyClicks)
        {
            Stop($"failed to withdraw Simulacrum stack after {MaxSupplyClicks} attempts");
            return;
        }
        if (BotMonotonicClock.ElapsedSince(_lastSupplyActionAt).TotalMilliseconds < 600)
            return;

        var rect = target.Rect.Value;
        _supplyTarget = $"tab={stash.VisibleTabIndex} stack={target.StackSize} "
            + $"rect={rect.X:F0},{rect.Y:F0},{rect.Width:F0},{rect.Height:F0}";
        var (x, y) = ctx.Snapshot.Window.ToScreen(
            (int)rect.CenterX, (int)rect.CenterY);
        var ticket = ctx.Input.ModifierClick(
            x, y, [VkLeftControl], ClickIntent.InteractUi,
            "withdraw Simulacrum stack",
            expectResolved: () => CountCarriedSupplies(_getSnapshot()?.Inventory) > 0,
            timeoutMs: 2000);
        if (ticket.Accepted)
        {
            _supplyClickAttempts++;
            _lastSupplyActionAt = BotMonotonicClock.Now;
            LastDecision = $"Supply: ctrl-clicked stack={target.StackSize} from tab {stash.VisibleTabIndex}";
            Diagnostics.EventLog.Emit(
                "simulacrum", "simulacrum.supply-withdraw-requested",
                Diagnostics.EventSeverity.Info, LastDecision,
                new Dictionary<string, object?>
                {
                    ["stack"] = target.StackSize,
                    ["tabIndex"] = stash.VisibleTabIndex,
                    ["path"] = target.Path,
                });
        }
    }

    private void TickDevice(BehaviorContext ctx)
    {
        if (ctx.Snapshot.Inventory.IsOpen)
        {
            _lastKnownInventoryCells = ctx.Snapshot.Inventory.OccupiedCells;
            _lastKnownInventoryCellsKnown = true;
            _carriedSupplies = CountCarriedSupplies(ctx.Snapshot.Inventory);
        }

        if (_entryTransition.State.Outcome != AreaTransitionOutcome.Idle)
        {
            var transition = _entryTransition.Observe(
                ctx.Snapshot.AreaHash,
                WorldAreaClassifier.Classify(ctx),
                AreaTransitionTracker.MonotonicNow(),
                TimeSpan.FromMilliseconds(LatencyPolicy.AllowanceMs(ctx.Settings)));
            if (transition.Outcome == AreaTransitionOutcome.Confirmed
                || ShouldAttachFreshUnknownMap(transition))
            {
                StartArena(ctx.Snapshot.AreaHash);
                LastDecision = transition.Outcome == AreaTransitionOutcome.Confirmed
                    ? "Device/Arena: entered and verified Simulacrum area"
                    : "Device/Arena: fresh portal changed area; locating Simulacrum monolith";
                if (transition.ObservedRole == AreaRole.Unknown)
                {
                    Diagnostics.EventLog.Emit(
                        "simulacrum", "simulacrum.entry-empty-bubble",
                        Diagnostics.EventSeverity.Info, LastDecision,
                        new Dictionary<string, object?>
                        {
                            ["originAreaHash"] = $"0x{transition.OriginAreaHash:X8}",
                            ["observedAreaHash"] = $"0x{transition.ObservedAreaHash:X8}",
                        });
                }
                return;
            }
            if (ctx.Snapshot.AreaHash != 0
                && ctx.Snapshot.AreaHash != transition.OriginAreaHash
                && transition.Outcome is AreaTransitionOutcome.WaitingForChange
                    or AreaTransitionOutcome.VerifyingDestination)
            {
                // The destination snapshot arrives before its nearby entities sometimes do.
                // Do not let the hideout-side device controller interpret the missing old
                // portal as a failure while destination-role evidence is still hydrating.
                LastDecision = $"Device/Transition: entered area 0x{ctx.Snapshot.AreaHash:X8}; verifying destination";
                return;
            }
            if (transition.Outcome is AreaTransitionOutcome.UnexpectedDestination
                or AreaTransitionOutcome.TimedOut)
            {
                Stop($"Simulacrum entry {transition.Outcome}: observed {transition.ObservedRole}");
                return;
            }
        }

        var result = _device.Tick(ctx);
        if (!_supplyCommittedToDevice
            && _device.CurrentPhase is MapDeviceSystem.Phase.Activate
                or MapDeviceSystem.Phase.WaitForPortals
                or MapDeviceSystem.Phase.EnterPortal)
        {
            // Simulacrums are unstackable in player inventory. Once the visible device slot
            // positively contains it, the carried item and its one occupied cell are gone.
            _supplyCommittedToDevice = true;
            _carriedSupplies = Math.Max(0, _carriedSupplies - 1);
            if (_lastKnownInventoryCellsKnown)
                _lastKnownInventoryCells = Math.Max(0, _lastKnownInventoryCells - 1);
            Diagnostics.EventLog.Emit(
                "simulacrum", "simulacrum.supply-staged",
                Diagnostics.EventSeverity.Info,
                "Simulacrum committed to visible map-device slot",
                new Dictionary<string, object?>
                {
                    ["carriedRemaining"] = _carriedSupplies,
                    ["inventoryCellsEstimate"] = _lastKnownInventoryCells,
                });
        }
        if (result == MapDeviceSystem.Result.Failed)
        {
            Stop($"Simulacrum device failed: {_device.Status}");
            return;
        }
        if (_device.CurrentPhase == MapDeviceSystem.Phase.EnterPortal
            && _entryTransition.State.Outcome == AreaTransitionOutcome.Idle)
        {
            _entryTransition.Start(
                ctx.Snapshot.AreaHash, AreaRole.SafeHub, AreaRole.Map,
                AreaTransitionTracker.MonotonicNow());
        }
        LastDecision = $"Device/{_device.CurrentPhase}: {_device.Status}";
    }

    private void TickArena(GameSnapshot snapshot, IInputRouter input, BehaviorContext ctx)
    {
        if (_arena.Phase == SimulacrumPhase.Terminal
            && snapshot.AreaHash != 0
            && snapshot.AreaHash != _arenaAreaHash)
        {
            var role = WorldAreaClassifier.Classify(ctx);
            if (role != AreaRole.SafeHub)
            {
                Stop($"Simulacrum exit reached unexpected destination {role}");
                return;
            }
            _runsCompleted++;
            Diagnostics.EventLog.Emit(
                "simulacrum", "simulacrum.run-completed",
                Diagnostics.EventSeverity.Info,
                $"completed Simulacrum {_runsCompleted}; returned to hideout",
                new Dictionary<string, object?> { ["runsCompleted"] = _runsCompleted });
            if (_settings.Current.SimulacrumTargetRuns > 0
                && _runsCompleted >= _settings.Current.SimulacrumTargetRuns)
            {
                Stop($"target Simulacrum count reached ({_runsCompleted})");
                return;
            }
            _arena.Reset();
            _device.Cancel();
            _entryTransition.Reset();
            _exitTransition.Reset();
            _step = Step.Supply;
            LastDecision = $"Arena/Supply: run {_runsCompleted} complete";
            return;
        }

        if (snapshot.AreaHash != _arenaAreaHash)
        {
            // The area hash can briefly read as zero/stale while a checkpoint revive swaps
            // top-level game states.  Treat that as transition evidence, not as proof that
            // the character left the arena.  A real premature departure must remain in a
            // positively classified destination before it is allowed to latch the run stop.
            var now = AreaTransitionTracker.MonotonicNow();
            if (_arenaMismatchSince == TimeSpan.MinValue)
                _arenaMismatchSince = now;
            var role = WorldAreaClassifier.Classify(ctx);
            var elapsed = now - _arenaMismatchSince;
            if (role == AreaRole.Unknown || elapsed < ArenaDepartureConfirmation)
            {
                LastDecision = $"Arena/Transition: awaiting stable destination ({role}, {elapsed.TotalMilliseconds:F0}ms)";
                return;
            }

            if (role == AreaRole.SafeHub && _deathRecoveryPending)
            {
                if (_abandonCurrentRun)
                {
                    CompleteAbandonToSupply(snapshot.AreaHash,
                        $"wave {_deathBudgetWave} death limit reached "
                        + $"({_waveDeaths}/{_settings.Current.SimulacrumMaxDeaths})");
                    return;
                }
                _step = Step.Recover;
                _recoveryStartedAt = now;
                _recoveryLeg = RecoveryLeg.ReturnToArena;
                _recoveryReason = $"checkpoint death {_runDeaths}";
                _recoveryOriginAreaHash = _arenaAreaHash;
                _returnThroughPortal.Reset();
                LastDecision = $"Recovery: returned to checkpoint after death {_runDeaths}; locating existing portal";
                Diagnostics.EventLog.Emit(
                    "simulacrum", "simulacrum.death-recovery-started",
                    Diagnostics.EventSeverity.Warning, LastDecision,
                    new Dictionary<string, object?>
                    {
                        ["runDeaths"] = _runDeaths,
                        ["waveDeaths"] = _waveDeaths,
                        ["wave"] = _deathBudgetWave,
                    });
                return;
            }

            Stop($"left Simulacrum before terminal completion (destination {role})");
            return;
        }

        _arenaMismatchSince = TimeSpan.MinValue;

        _arena.Tick(snapshot, input);
        SyncWaveDeathBudget();
        if (_arena.PortalRefreshRequested)
        {
            _step = Step.Recover;
            _recoveryStartedAt = AreaTransitionTracker.MonotonicNow();
            _recoveryLeg = RecoveryLeg.ExitArena;
            _recoveryReason = $"wave {_arena.ActiveWave} dry-sweep portal refresh";
            _recoveryOriginAreaHash = snapshot.AreaHash;
            _returnThroughPortal.Reset();
            LastDecision = "Recovery/Exit: dry-sweep budget exhausted; leaving and re-entering same instance";
            Diagnostics.EventLog.Emit(
                "simulacrum", "simulacrum.portal-refresh-started",
                Diagnostics.EventSeverity.Warning, LastDecision,
                new Dictionary<string, object?>
                {
                    ["wave"] = _arena.ActiveWave,
                    ["completedSweeps"] = _arena.WaveSweepPass,
                    ["areaHash"] = $"0x{snapshot.AreaHash:X8}",
                });
            return;
        }
        if (_arena.IsFailed)
        {
            if (_arena.FailureReason.Contains("active-wave limit", StringComparison.OrdinalIgnoreCase))
                BeginArenaAbandon(snapshot.AreaHash, _arena.FailureReason);
            else
                Stop($"Simulacrum arena failed: {_arena.FailureReason}");
            return;
        }
        if (_arena.Phase == SimulacrumPhase.Terminal
            && _exitTransition.State.Outcome == AreaTransitionOutcome.Idle)
        {
            _exitTransition.Start(
                snapshot.AreaHash, AreaRole.Map, AreaRole.SafeHub,
                AreaTransitionTracker.MonotonicNow());
        }
        LastDecision = $"Arena/{_arena.Phase}: {_arena.LastDecision}";
    }

    private void TickRecovery(BehaviorContext ctx)
    {
        if (_recoveryStartedAt != TimeSpan.MinValue
            && AreaTransitionTracker.MonotonicNow() - _recoveryStartedAt > TimeSpan.FromSeconds(30))
        {
            Stop($"Simulacrum recovery timed out during {_recoveryLeg}: {_recoveryReason}");
            return;
        }

        // The portal-entered latch only has meaning while returning to the arena.
        if (_recoveryLeg != RecoveryLeg.ReturnToArena) _reentryPortalEntered = false;

        if (_recoveryLeg == RecoveryLeg.AwaitSafeHub)
        {
            var role = WorldAreaClassifier.Classify(ctx);
            if (ctx.Snapshot.AreaHash != 0
                && ctx.Snapshot.AreaHash != _recoveryOriginAreaHash
                && role == AreaRole.SafeHub)
            {
                if (_discardExistingRun || _abandonCurrentRun)
                {
                    if (_abandonCurrentRun)
                        CompleteAbandonToSupply(ctx.Snapshot.AreaHash, _recoveryReason);
                    else
                        CompleteDiscardToSupply(ctx.Snapshot.AreaHash);
                    return;
                }
                _recoveryLeg = RecoveryLeg.ReturnToArena;
                _recoveryStartedAt = AreaTransitionTracker.MonotonicNow();
                _returnThroughPortal.Reset();
                LastDecision = $"Recovery/Return: hideout confirmed after {_recoveryReason}; entering existing portal";
                Diagnostics.EventLog.Emit(
                    "simulacrum", "simulacrum.portal-refresh-hideout-confirmed",
                    Diagnostics.EventSeverity.Info, LastDecision,
                    new Dictionary<string, object?>
                    {
                        ["areaHash"] = $"0x{ctx.Snapshot.AreaHash:X8}",
                        ["role"] = role.ToString(),
                    });
                return;
            }
            LastDecision = $"Recovery/AwaitSafeHub: waiting for stable hideout evidence ({role})";
            return;
        }

        if (_recoveryLeg == RecoveryLeg.ExitArena)
        {
            if (FindExistingPortal(ctx) is null && _arena.PortalAnchor is null)
            {
                if (ctx.Live is { } searching)
                    _recoveryExploration.TrackVisit(ctx.Snapshot, searching.GridPosition);
                if (_recoveryExploration.IsExhausted)
                {
                    Stop($"could not locate an exit portal while abandoning: {_recoveryReason}");
                    return;
                }
                _recoveryExplore.Tick(ctx);
                LastDecision = $"Recovery/Exit: searching arena for portal ({_recoveryExploration.LastFrontierReason})";
                return;
            }
            var result = _returnThroughPortal.Tick(ctx);
            if (result == BehaviorStatus.Success)
            {
                _recoveryLeg = RecoveryLeg.AwaitSafeHub;
                _recoveryStartedAt = AreaTransitionTracker.MonotonicNow();
                _returnThroughPortal.Reset();
                LastDecision = $"Recovery/AwaitSafeHub: exited arena for {_recoveryReason}; confirming hideout";
                Diagnostics.EventLog.Emit(
                    "simulacrum", "simulacrum.portal-refresh-exited",
                    Diagnostics.EventSeverity.Info, LastDecision);
                return;
            }
            LastDecision = $"Recovery/Exit: locating arena portal for {_recoveryReason}";
            return;
        }

        // RecoveryLeg.ReturnToArena — two-phase: (1) click the existing portal, (2) WAIT for the
        // arena area to actually load before reattaching. Latching the still-hideout hash here is
        // the wave-11 hang bug; only reattach once we're positively in a changed, non-hub area.
        if (!_reentryPortalEntered)
        {
            if (_returnThroughPortal.Tick(ctx) != BehaviorStatus.Success)
            {
                LastDecision = $"Recovery/Return: locating and entering existing portal after {_recoveryReason}";
                return;
            }
            _reentryPortalEntered = true;
            _returnThroughPortal.Reset();
            LastDecision = "Recovery/Return: portal entered; awaiting arena load";
            return;
        }

        var arenaRole = WorldAreaClassifier.Classify(ctx);
        if (ctx.Snapshot.AreaHash == 0
            || ctx.Snapshot.AreaHash == _recoveryOriginAreaHash
            || arenaRole == AreaRole.SafeHub)
        {
            LastDecision = $"Recovery/Return: awaiting arena (area 0x{ctx.Snapshot.AreaHash:X8}, {arenaRole})";
            return;
        }

        var reason = _recoveryReason;
        ReattachArena(
            ctx.Snapshot.AreaHash,
            preserveSweepProgress: reason.StartsWith(
                "checkpoint death", StringComparison.OrdinalIgnoreCase));
        _reentryPortalEntered = false;
        LastDecision = $"Recovery/Arena: re-entered existing instance after {reason}";
        Diagnostics.EventLog.Emit(
            "simulacrum", "simulacrum.portal-recovery-completed",
            Diagnostics.EventSeverity.Info, LastDecision,
            new Dictionary<string, object?>
            {
                ["runDeaths"] = _runDeaths,
                ["reason"] = reason,
                ["areaHash"] = $"0x{ctx.Snapshot.AreaHash:X8}",
            });
    }

    private void BeginArenaAbandon(uint arenaAreaHash, string reason)
    {
        _abandonCurrentRun = true;
        _deathRecoveryPending = false;
        _step = Step.Recover;
        _recoveryStartedAt = AreaTransitionTracker.MonotonicNow();
        _recoveryLeg = RecoveryLeg.ExitArena;
        _recoveryReason = reason;
        _recoveryOriginAreaHash = arenaAreaHash;
        _returnThroughPortal.Reset();
        _recoveryExploration.Reset();
        _recoveryExplore.Reset();
        LastDecision = $"Abandon/Exit: {reason}; leaving for a fresh Simulacrum";
        Diagnostics.EventLog.Emit(
            "simulacrum", "simulacrum.run-abandon-started",
            Diagnostics.EventSeverity.Warning, LastDecision,
            new Dictionary<string, object?>
            {
                ["wave"] = _arena.ActiveWave,
                ["waveDeaths"] = _waveDeaths,
                ["reason"] = reason,
                ["areaHash"] = $"0x{arenaAreaHash:X8}",
            });
    }

    private void CompleteAbandonToSupply(uint hideoutAreaHash, string reason)
    {
        _runsAbandoned++;
        ResetArenaForFreshSupply();
        LastDecision = $"Abandon/Supply: {reason}; starting fresh supplied run";
        Diagnostics.EventLog.Emit(
            "simulacrum", "simulacrum.run-abandoned",
            Diagnostics.EventSeverity.Warning, LastDecision,
            new Dictionary<string, object?>
            {
                ["hideoutAreaHash"] = $"0x{hideoutAreaHash:X8}",
                ["runsStarted"] = _runsStarted,
                ["runsCompleted"] = _runsCompleted,
                ["runsAbandoned"] = _runsAbandoned,
                ["reason"] = reason,
            });
    }

    private void CompleteDiscardToSupply(uint hideoutAreaHash)
    {
        _settings.Mutate(settings => settings.SimulacrumDiscardExistingRun = false);
        ResetArenaForFreshSupply();
        LastDecision = "Discard/Supply: old arena exited; starting fresh supplied run";
        Diagnostics.EventLog.Emit(
            "simulacrum", "simulacrum.discard-completed",
            Diagnostics.EventSeverity.Info, LastDecision,
            new Dictionary<string, object?>
            {
                ["hideoutAreaHash"] = $"0x{hideoutAreaHash:X8}",
            });
    }

    private void ResetArenaForFreshSupply()
    {
        _arena.Reset();
        _device.Cancel();
        _entryTransition.Reset();
        _exitTransition.Reset();
        _arenaAreaHash = 0;
        _arenaMismatchSince = TimeSpan.MinValue;
        _deathRecoveryPending = false;
        _runDeaths = 0;
        _waveDeaths = 0;
        _deathBudgetWave = 0;
        _recoveryStartedAt = TimeSpan.MinValue;
        _recoveryLeg = RecoveryLeg.ReturnToArena;
        _recoveryReason = string.Empty;
        _recoveryOriginAreaHash = 0;
        _returnThroughPortal.Reset();
        _recoveryExploration.Reset();
        _recoveryExplore.Reset();
        _discardExistingRun = false;
        _abandonCurrentRun = false;
        _step = Step.Supply;
    }

    private void StartArena(uint areaHash)
    {
        _device.Cancel();
        _arena.Reset();
        _arenaMismatchSince = TimeSpan.MinValue;
        if (_lastKnownInventoryCellsKnown)
            _arena.SeedInventoryOccupancy(_lastKnownInventoryCells);
        _arenaAreaHash = areaHash;
        _runDeaths = 0;
        _waveDeaths = 0;
        _deathBudgetWave = 0;
        _deathRecoveryPending = false;
        _abandonCurrentRun = false;
        _recoveryStartedAt = TimeSpan.MinValue;
        _recoveryLeg = RecoveryLeg.ReturnToArena;
        _recoveryReason = string.Empty;
        _recoveryOriginAreaHash = 0;
        _runsStarted++;
        _step = Step.Arena;
    }

    private void SyncWaveDeathBudget()
    {
        if (_arena.Phase != SimulacrumPhase.Fighting) return;
        var wave = _arena.ActiveWave;
        if (wave <= 0 || wave == _deathBudgetWave) return;
        _deathBudgetWave = wave;
        _waveDeaths = 0;
        _arena.SeedDeathCount(0);
        Diagnostics.EventLog.Emit(
            "simulacrum", "simulacrum.wave-budget-reset",
            Diagnostics.EventSeverity.Info,
            $"wave {wave} budgets reset: 0/{_settings.Current.SimulacrumMaxDeaths} deaths, "
                + $"{_arena.ActiveWaveLimit.TotalSeconds:F0}s",
            new Dictionary<string, object?>
            {
                ["wave"] = wave,
                ["maxDeathsPerWave"] = _settings.Current.SimulacrumMaxDeaths,
                ["waveLimitSeconds"] = _arena.ActiveWaveLimit.TotalSeconds,
            });
    }

    private void ReattachArena(uint areaHash, bool preserveSweepProgress = false)
    {
        _arena.ResetForReattach(preserveSweepProgress);
        if (_lastKnownInventoryCellsKnown)
            _arena.SeedInventoryOccupancy(_lastKnownInventoryCells);
        _arena.SeedDeathCount(_waveDeaths);
        _arenaAreaHash = areaHash;
        _arenaMismatchSince = TimeSpan.MinValue;
        _deathRecoveryPending = false;
        _recoveryStartedAt = TimeSpan.MinValue;
        _recoveryLeg = RecoveryLeg.ReturnToArena;
        _recoveryReason = string.Empty;
        _recoveryOriginAreaHash = 0;
        _returnThroughPortal.Reset();
        _step = Step.Arena;
    }

    private void Stop(string reason)
    {
        _stopped = true;
        _step = Step.Stopped;
        _stopReason = reason;
        _movement.Release();
        _interact.Cancel();
        _device.Cancel();
        _settings.Mutate(settings => settings.BotActive = false);
        LastDecision = $"STOPPED: {reason}";
        Diagnostics.EventLog.Emit(
            "simulacrum", "simulacrum.loop-stopped",
            Diagnostics.EventSeverity.Warning, reason,
            new Dictionary<string, object?>
            {
                ["runsStarted"] = _runsStarted,
                ["runsCompleted"] = _runsCompleted,
            });
    }

    private static int CountCarriedSupplies(InventoryView? inventory)
    {
        if (inventory is null || !inventory.IsOpen) return 0;
        var total = 0;
        foreach (var item in inventory.Items)
            if (item.Path.Contains(
                    InventoryView.SimulacrumPathFragment,
                    StringComparison.OrdinalIgnoreCase))
                total += Math.Max(1, item.StackSize);
        return total;
    }

    private static EntityCache.Entry? FindStash(BehaviorContext ctx)
        => ctx.Entities?.Entries.Values.FirstOrDefault(entity =>
            !entity.IsStale && entity.Kind == EntityListReader.EntityKind.Stash);

    private static EntityCache.Entry? FindExistingPortal(BehaviorContext ctx)
        => ctx.Entities?.Entries.Values.FirstOrDefault(entity =>
            !entity.IsStale && entity.Kind is EntityListReader.EntityKind.TownPortal
                or EntityListReader.EntityKind.Portal);

    private static EntityCache.Entry? FindMonolith(BehaviorContext ctx)
        => ctx.Entities?.Entries.Values.FirstOrDefault(entity =>
            !entity.IsStale
            && entity.Path.Contains("Objects/Afflictionator", StringComparison.OrdinalIgnoreCase));
}
