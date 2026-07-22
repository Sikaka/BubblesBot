using BubblesBot.Bot.Behaviors;
using BubblesBot.Bot.Behaviors.Interact;
using BubblesBot.Bot.Input;
using BubblesBot.Bot.Settings;
using BubblesBot.Bot.Strategies;
using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Modes;

public enum MapRunPhase
{
    Preparation,
    Deposit,
    Device,
    Entry,
    Clear,
    BossMechanics,
    Completion,
    Exit,
    Report,
    Stopped,
}

/// <summary>
/// Stacked-deck farming orchestrator: <b>load a staged map → enter → clear → leave via Town
/// Portal → stash loot → repeat</b>. Chains the existing pieces — <see cref="MapDeviceSystem"/>
/// (load), <see cref="PushCombatMode"/> in orchestrated mode (clear),
/// <see cref="LeaveMapSystem"/> (F-key exit), <see cref="StashDepositSystem"/> (deposit).
///
/// <para><b>Spans area transitions.</b> Unlike the other modes, this one is intentionally NOT
/// reset by <c>BotApp</c> on area change — it has to remember its place across hideout↔map
/// boundaries and keep cross-map counters. It watches the area hash itself and, on each
/// transition, resets its sub-systems (which ARE per-area) while preserving the high-level
/// step and the maps-completed / items-stashed tallies.</para>
///
/// <para><b>Stop conditions</b> (each disarms the bot via <see cref="BotSettings.BotActive"/>):
/// target map count reached, storage empty (no maps left), out of mapping resources (the F-key
/// produces no Town Portal → almost certainly out of Portal Scrolls), or player death. After a
/// stop, re-arming starts a fresh run.</para>
/// </summary>
public sealed class MapRunMode : IBotMode
{
    private enum Step { Boot, Hideout, EnterMapWait, Map, Recover, LeaveWait, Report }
    private enum RecoveryLeg { None, AwaitSafeHub, ReturnToMap }

    private readonly SettingsStore        _settings;
    private readonly StrategyStore        _strategies;
    private readonly Func<GameSnapshot?>  _getSnapshot;
    private readonly Func<LivePlayer?>    _getLive;
    private readonly Func<EntityCache?>   _getEntities;
    private readonly Diagnostics.IRunReporter _reporter;
    private readonly Func<LootLedger.SnapshotData> _getLoot;

    // Per-map run-report accounting (wall-clock; reports are wall-clock, not monotonic).
    private DateTime _mapStartedUtc = DateTime.UtcNow;
    private TimeSpan _mapStartedAt = TimeSpan.Zero;
    private float _lootChaosAtMapStart;
    private int _pickupsAtMapStart;
    private int _mapIndex;
    private bool _mapReportEmitted;
    private bool _abandoningMap;
    private string _abandonReason = "";

    /// <summary>
    /// The strategy pinned for the current run. Refreshed from <see cref="StrategyStore.Active"/>
    /// only while in hideout (the transaction boundary), so swapping the active strategy mid-map
    /// takes effect on the NEXT map rather than mutating a run in flight.
    /// </summary>
    private FarmingStrategy? _activeStrategy;

    private readonly MovementSystem    _movement;
    private readonly SkillBook         _skills;
    private readonly InteractSystem    _interact = new();
    private readonly MapDeviceSystem   _mapDevice;
    private readonly LeaveMapSystem    _leaveMap;
    private readonly StashDepositSystem _stashDeposit;
    private readonly StashTabSwitcher  _supplyTabSwitcher;
    private readonly PushCombatMode    _mapFarming;
    private readonly EnterAreaTransition _returnThroughCheckpoint;
    private readonly AreaTransitionTracker _entryTransition = new(TimeSpan.FromSeconds(8));

    private Step     _step = Step.Boot;
    private uint     _lastAreaHash;
    private bool     _needDeposit;
    private TimeSpan _deviceFailedAt;
    private int _deviceFailures;
    private int _supplyClickAttempts;
    private readonly HashSet<int> _supplyPanelsExamined = [];
    private int _supplyTierClickAttempts;
    private TimeSpan _supplyTabObservedAt = TimeSpan.MinValue;
    private TimeSpan _lastSupplyActionAt = TimeSpan.MinValue;
    private RecoveryLeg _recoveryLeg;
    private TimeSpan _recoveryStartedAt = TimeSpan.MinValue;
    private uint _recoveryOriginAreaHash;
    private int _mapDeaths;

    // Cross-run telemetry / counters (survive area changes).
    private int      _mapsCompleted;
    private int      _itemsStashed;
    private int      _portalScrollsRemaining;
    private bool     _stopped;
    private string   _stopReason = "";
    private string   _phase = "init";
    private MapRunPhase _lifecyclePhase = MapRunPhase.Preparation;
    private string   _runId = Guid.NewGuid().ToString("N");

    private const int VK_ESCAPE          = 0x1B;
    private const int VK_LCONTROL        = 0xA2;
    private const int DeviceRetryBackoffSec = 5;
    private const int MaxDeviceFailures = 3;

    public string Name => "Map farming";
    public IBehavior Root => _mapFarming.Root;   // dashboard shows the clear tree (most active sub-flow)
    public string LastDecision { get; private set; } = "init";

    // ── Telemetry surface (read by BotApp.BuildStatus → "loop" block) ──
    // Telemetry falls back to the store's active strategy so the dashboard shows the selected
    // strategy even before the first armed tick pins it for a run.
    private FarmingStrategy? TelemetryStrategy => _activeStrategy ?? _strategies.Active;

    public string LoopPhase => _phase;
    public MapRunPhase LifecyclePhase => _lifecyclePhase;
    public string Preset => TelemetryStrategy?.Identity.Name ?? "(no strategy)";
    public string ResourcePolicy => (TelemetryStrategy?.Loot.DepositAfterEachMap ?? false)
        ? "deposit-after-map" : "continuous-until-inventory-stop";
    public string CurrentStep => _step.ToString();
    public int  MapsCompleted => _mapsCompleted;
    public int  TargetMaps => TelemetryStrategy?.Completion.TargetMaps ?? 0;
    public int  ItemsStashed => _itemsStashed + (_stashDeposit.IsBusy ? _stashDeposit.Deposited : 0);
    public int  PortalScrollsRemaining => _portalScrollsRemaining;
    public bool IsStopped => _stopped;
    public string StopReason => _stopReason;
    public string RunId => _runId;
    public AreaTransitionState EntryTransition => _entryTransition.State;
    public AreaTransitionState ExitTransition => _leaveMap.Transition;
    public object? MapTelemetry => _mapFarming.Telemetry;
    public IReadOnlyList<string> HudLines => _mapFarming.HudLines;

    public MapRunMode(SettingsStore settings, CombatCoordinator coord, StrategyStore strategies,
        Func<GameSnapshot?> getSnapshot,
        Func<LivePlayer?> getLive, Func<EntityCache?> getEntities,
        Diagnostics.IRunReporter reporter, Func<LootLedger.SnapshotData> getLoot)
    {
        _settings    = settings;
        _strategies  = strategies;
        _getSnapshot = getSnapshot;
        _getLive     = getLive;
        _getEntities = getEntities;
        _reporter    = reporter;
        _getLoot     = getLoot;
        // One shared movement/skills authority with the combat brain.
        _movement    = coord.Movement;
        _skills      = coord.Skills;
        _mapDevice   = new MapDeviceSystem(_movement, _skills, getSnapshot, getLive, getEntities);
        _leaveMap    = new LeaveMapSystem(_movement, () => settings.Current.StackedDeckPortalKeyVk, IsHideout);
        _stashDeposit = new StashDepositSystem(
            _movement, _skills, getSnapshot,
            (inventory, item) => MapInventoryPolicy.ShouldRetainForNextRun(
                inventory.Items, item, _activeStrategy));
        _supplyTabSwitcher = new StashTabSwitcher(getSnapshot);
        _mapFarming  = new PushCombatMode(settings, coord, getSnapshot, getLive, getEntities,
            orchestrated: true, getStrategy: () => _activeStrategy);
        _returnThroughCheckpoint = new EnterAreaTransition(
            "return through boss checkpoint portal", _interact, _movement, _skills,
            getSnapshot,
            entity => entity.Kind is EntityListReader.EntityKind.TownPortal
                or EntityListReader.EntityKind.Portal);
    }

    public void Reset() => ResetRun();

    /// <summary>
    /// Called by the global revive gate on confirmed resurrection. A positively confirmed
    /// pre-boss portal keeps this map armed and enters recovery; false preserves the global
    /// fail-safe stop for deaths without a usable checkpoint.
    /// </summary>
    public bool NotifyRevived()
    {
        if (_stopped || _step != Step.Map || !_mapFarming.PrepareForBossCheckpointRecovery())
            return false;

        _mapDeaths++;
        _recoveryLeg = RecoveryLeg.AwaitSafeHub;
        _recoveryStartedAt = BotMonotonicClock.Now;
        _recoveryOriginAreaHash = _lastAreaHash;
        _returnThroughCheckpoint.Reset();
        _step = Step.Recover;
        _lifecyclePhase = MapRunPhase.Entry;
        _phase = $"death {_mapDeaths}: awaiting hideout, then boss checkpoint re-entry";
        Diagnostics.EventLog.Emit(
            "maprun", "maprun.boss-checkpoint-recovery-started",
            Diagnostics.EventSeverity.Warning, _phase,
            new Dictionary<string, object?>
            {
                ["deaths"] = _mapDeaths,
                ["originAreaHash"] = $"0x{_recoveryOriginAreaHash:X8}",
            });
        return true;
    }

    public void NotifyDeath()
    {
        if (!_stopped)
        {
            _mapDeaths++;
            Stop("player died", "died");
        }
    }

    /// <summary>Called on the tick-thread when an armed mapping run is manually disarmed.</summary>
    public void NotifyDisarmed()
    {
        if (!_stopped)
            Stop("automation manually disarmed", "disarmed");
    }

    public void Tick(GameSnapshot snapshot, IInputRouter input)
    {
        if (snapshot.Player is { } pv) _skills.SetActorContext(pv.ActorComponentAddress);
        if (_skills.CooldownReader is null) _skills.CooldownReader = new SkillCooldownReader(snapshot.Reader);

        // Pin the active strategy while in a safe hub (the run transaction boundary); keep the
        // pinned one for the rest of the run so a mid-map swap only takes effect next map. A run
        // with no valid strategy fails loud — never silently maps with defaults.
        var pinned = new BehaviorContext(snapshot, input, _settings.Current, _getLive(), _getEntities());
        if (_activeStrategy is null || IsHideout(pinned)) _activeStrategy = _strategies.Active;

        var ctx = new BehaviorContext(snapshot, input, _settings.Current, _getLive(), _getEntities(), _activeStrategy);

        // A stopped run reaches this method only after the user explicitly re-arms it.
        if (_stopped) ResetRun();

        if (_activeStrategy is null && !_stopped)
        {
            Stop("no active farming strategy — select one in the web UI (Strategies tab) before arming map farming");
            return;
        }

        // Internal area-change handling — we are NOT auto-reset, so detect transitions here.
        var hash = snapshot.AreaHash;
        if (hash != 0 && hash != _lastAreaHash)
        {
            if (_lastAreaHash != 0) OnAreaChanged(ctx);
            _lastAreaHash = hash;
        }

        if (_stopped) return;

        // Death → stop + disarm.
        if (ctx.Live is { } lv && lv.HpMax > 0 && lv.HpCurrent <= 0)
        {
            Stop("player died");
            return;
        }

        // Refresh portal-scroll telemetry whenever the inventory is readable (hideout / stash open).
        var inv = snapshot.Inventory;
        if (inv.IsOpen) _portalScrollsRemaining = inv.PortalScrollCount();

        switch (_step)
        {
            case Step.Boot:         TickBoot(ctx); break;
            case Step.Hideout:      TickHideout(ctx); break;
            case Step.EnterMapWait: TickEnterMapWait(ctx); break;
            case Step.Map:          TickMap(snapshot, input, ctx); break;
            case Step.Recover:      TickRecovery(ctx); break;
            case Step.LeaveWait:    TickLeave(ctx); break;
            case Step.Report:       TickReport(); break;
        }

        LastDecision = $"step={_step} {_phase} maps={_mapsCompleted}/{TargetMaps}";
    }

    // ─── Steps ───────────────────────────────────────────────────────────

    private void TickBoot(BehaviorContext ctx)
    {
        _lifecyclePhase = MapRunPhase.Preparation;
        if (IsHideout(ctx))
        {
            _needDeposit = ShouldDeposit;
            _step = Step.Hideout;
            _phase = "in hideout";
        }
        else if (ctx.Snapshot.Player is not null)
        {
            // Armed inside a map already — pick up the clear loop.
            _step = Step.Map;
            _phase = "in map";
            MarkMapStart();
        }
        else
        {
            _phase = "waiting to classify area";
        }
    }

    private void TickHideout(BehaviorContext ctx)
    {
        var snapshot = ctx.Snapshot;

        // A failed/retried map-device attempt can leave the Atlas covering the world.
        // Stash interactions behind that panel cannot resolve, so close it before the
        // deposit preflight owns movement/clicks.
        if (_needDeposit && snapshot.AtlasPanel.IsVisible && !snapshot.IsStashOpen)
        {
            ctx.Input.VerifiedTapKey(
                VK_ESCAPE, ClickIntent.InteractUi, "close atlas before map deposit",
                expectResolved: () => !(_getSnapshot()?.AtlasPanel.IsVisible ?? true),
                timeoutMs: 2000);
            _phase = "closing atlas before deposit";
            return;
        }

        // 1. Deposit loot (opens the stash, which also opens the inventory so items read).
        if (_needDeposit && ShouldDeposit)
        {
            _lifecyclePhase = MapRunPhase.Deposit;
            if (!_stashDeposit.IsBusy
                && _stashDeposit.CurrentPhase is not (StashDepositSystem.Phase.Done or StashDepositSystem.Phase.Failed))
                _stashDeposit.Start(_activeStrategy?.Supply.DumpTabName ?? "");

            var r = _stashDeposit.Tick(ctx);
            _phase = $"deposit: {_stashDeposit.Status}";
            if (r == StashDepositSystem.Result.InProgress) return;

            _itemsStashed += _stashDeposit.Deposited;
            if (r == StashDepositSystem.Result.Failed)
            {
                Stop($"stash deposit incomplete: {_stashDeposit.Status}");
                return;
            }
            _needDeposit = false;
        }

        // Player-inventory strategies normally carry their whole queue. If none remains,
        // replenish one exact unrolled target from the named supply tab. Besides ordinary
        // restocking, this makes a deposit/restart transaction recoverable without manual UI.
        if (_activeStrategy?.Supply.Map is { Source: MapSource.PlayerInventory, RestockFromStash: true } mapSupply
            && snapshot.IsStashOpen
            && CountCarriedTargetMaps(snapshot.Inventory) < mapSupply.CarriedMapBuffer)
        {
            TickWithdrawCarriedMap(ctx);
            return;
        }

        // 2. Close any open panel (stash/inventory) before clicking the map device in-world.
        if (snapshot.IsStashOpen)
        {
            ctx.Input.TapKey(VK_ESCAPE, ClickIntent.InteractUi, "close stash");
            _phase = "closing panels";
            return;
        }

        // 3. Stop if we've hit the target map count.
        if (_mapsCompleted >= (_activeStrategy?.Completion.TargetMaps ?? int.MaxValue))
        {
            Stop($"target map count reached ({_mapsCompleted})");
            return;
        }

        // 4. Load the next map via the device flow (with a small backoff after a failure).
        _lifecyclePhase = MapRunPhase.Device;
        if (_mapDevice.CurrentPhase == MapDeviceSystem.Phase.Failed)
        {
            if (_deviceFailures >= MaxDeviceFailures)
            {
                Stop($"map device retry budget exhausted ({_deviceFailures}/{MaxDeviceFailures}): {_mapDevice.Status}");
                return;
            }
            if ((BotMonotonicClock.Now - _deviceFailedAt).TotalSeconds < DeviceRetryBackoffSec)
            {
                _phase = $"map device failed — retrying soon ({_mapDevice.Status})";
                return;
            }
            _mapDevice.Start(ctx.Entities, MapPayloadSource());
        }
        else if (!_mapDevice.IsBusy)
        {
            _mapDevice.Start(ctx.Entities, MapPayloadSource());
        }

        // Storage-empty detection is owned by MapDeviceSystem.TickSelectMap ("no maps in
        // storage", handled below). A mode-level "atlas visible && storage empty" check is
        // WRONG: ctrl+clicking the LAST stored map into the device empties storage while a
        // map sits staged, and the check killed the loop right there (live 2026-07-15).

        var dr = _mapDevice.Tick(ctx);
        if (dr == MapDeviceSystem.Result.Failed)
        {
            _deviceFailedAt = BotMonotonicClock.Now;
            _deviceFailures++;
            if (_mapDevice.Status.Contains("no maps", StringComparison.OrdinalIgnoreCase))
            {
                Stop("out of maps (storage empty)");
                return;
            }
        }

        // Once the device flow is walking into the portal, the area change is imminent.
        if (_mapDevice.CurrentPhase == MapDeviceSystem.Phase.EnterPortal)
        {
            _entryTransition.Start(snapshot.AreaHash, AreaRole.SafeHub, AreaRole.Map,
                AreaTransitionTracker.MonotonicNow());
            _step = Step.EnterMapWait;
        }

        _phase = $"load: {_mapDevice.Status}";
    }

    private MapDeviceSystem.PayloadSource MapPayloadSource()
        => _activeStrategy?.Supply.Map.Source == MapSource.PlayerInventory
            ? MapDeviceSystem.PayloadSource.InventoryNormalMap
            : MapDeviceSystem.PayloadSource.AtlasStorage;

    private int CountCarriedTargetMaps(InventoryView inventory)
    {
        return inventory.IsOpen
            ? inventory.Items.Count(item => InventoryView.IsNormalTierMap(item, 16))
            : 0;
    }

    private void TickWithdrawCarriedMap(BehaviorContext ctx)
    {
        var strategy = _activeStrategy!;
        var tabName = strategy.Supply.SuppliesTabName.Trim();
        if (tabName.Length == 0)
        {
            Stop("player-inventory map supply tab is empty");
            return;
        }

        var targetTab = ctx.Snapshot.StashTabs.Find(tabName, requireGeneralPurpose: false);
        if (targetTab is null)
        {
            Stop($"map supply tab '{tabName}' not found");
            return;
        }
        var stash = ctx.Snapshot.StashInventory;
        if (ctx.Snapshot.StashTabs.FindSelected(
                tabName, requireGeneralPurpose: false, stash.VisibleTabIndex) is null)
        {
            if (!_supplyTabSwitcher.IsStarted
                || !_supplyTabSwitcher.TargetName.Equals(tabName, StringComparison.OrdinalIgnoreCase))
                _supplyTabSwitcher.Start(tabName, requireGeneralPurpose: false);
            var switched = _supplyTabSwitcher.Tick(ctx);
            _phase = $"map supply: {_supplyTabSwitcher.Status}";
            if (switched == StashTabSwitcher.Result.Failed)
                Stop($"map supply tab switch failed: {_supplyTabSwitcher.Status}");
            return;
        }
        if (_supplyTabSwitcher.IsStarted)
        {
            _supplyTabSwitcher.Reset();
            _supplyTabObservedAt = BotMonotonicClock.Now;
            _phase = $"map supply: on '{tabName}', settling items";
            return;
        }
        if (_supplyTabObservedAt == TimeSpan.MinValue)
        {
            _supplyTabObservedAt = BotMonotonicClock.Now;
            _phase = $"map supply: '{tabName}' visible, settling items";
            return;
        }
        if (BotMonotonicClock.ElapsedSince(_supplyTabObservedAt).TotalMilliseconds < 800)
            return;

        var target = stash.Items
            .Where(item => item.Rect is not null && StashInventoryView.IsNormalTierMap(item, 16))
            .OrderBy(item => item.Rect!.Value.Y)
            .ThenBy(item => item.Rect!.Value.X)
            .FirstOrDefault();
        if (target.ItemEntity == 0 || target.Rect is null)
        {
            var anyMap = stash.Items.FirstOrDefault(item =>
                item.Path.Contains(InventoryView.MapPathFragment, StringComparison.OrdinalIgnoreCase));
            var viewingTier16 = anyMap.ItemEntity != 0
                && anyMap.Path.Contains("MapKeyTier16", StringComparison.OrdinalIgnoreCase);
            var navigation = ctx.Snapshot.MapStashNavigation;
            if (!viewingTier16)
            {
                if (_supplyTierClickAttempts >= 3 || navigation.Tier16Rect is not { } tierRect)
                {
                    Stop("specialized Maps tab is not on T16 and its T16 control is unreadable");
                    return;
                }
                var (tierX, tierY) = ctx.Snapshot.Window.ToScreen(tierRect.CenterX, tierRect.CenterY);
                var tierTicket = ctx.Input.Click(
                    tierX, tierY, ClickIntent.InteractUi, "select exact T16 map-stash tier control",
                    expectResolved: () => _getSnapshot()?.StashInventory.Items.Any(item =>
                        item.Path.Contains("MapKeyTier16", StringComparison.OrdinalIgnoreCase)) ?? false,
                    timeoutMs: 2000);
                if (tierTicket.Accepted)
                {
                    _supplyTierClickAttempts++;
                    _lastSupplyActionAt = BotMonotonicClock.Now;
                    _supplyTabObservedAt = BotMonotonicClock.Now;
                    _phase = $"map supply: selecting T16 tier ({_supplyTierClickAttempts}/3)";
                }
                return;
            }

            if (navigation.IsReadable && navigation.CurrentPanelIndex >= 0)
                _supplyPanelsExamined.Add(navigation.CurrentPanelIndex);
            var nextPanel = navigation.Selectors.FirstOrDefault(selector =>
                !_supplyPanelsExamined.Contains(selector.PanelIndex));
            if (nextPanel is not null)
            {
                var (panelX, panelY) = ctx.Snapshot.Window.ToScreen(
                    nextPanel.Rect.CenterX, nextPanel.Rect.CenterY);
                var panelTicket = ctx.Input.Click(
                    panelX, panelY, ClickIntent.InteractUi,
                    $"select exact map-stash subinventory {nextPanel.Label}",
                    expectResolved: () => _getSnapshot()?.MapStashNavigation.CurrentPanelIndex
                        == nextPanel.PanelIndex,
                    timeoutMs: 1800);
                if (panelTicket.Accepted)
                {
                    _lastSupplyActionAt = BotMonotonicClock.Now;
                    _supplyTabObservedAt = BotMonotonicClock.Now;
                    _phase = $"map supply: selecting T16 subinventory {nextPanel.Label}";
                    Diagnostics.EventLog.Emit(
                        "maprun", "maprun.map-supply-subinventory-requested",
                        Diagnostics.EventSeverity.Info, _phase,
                        new Dictionary<string, object?>
                        {
                            ["panelIndex"] = nextPanel.PanelIndex,
                            ["label"] = nextPanel.Label,
                            ["examined"] = string.Join(',', _supplyPanelsExamined.Order()),
                        });
                }
                return;
            }

            var observed = string.Join(", ", stash.Items
                .Where(item => item.Path.Contains(InventoryView.MapPathFragment, StringComparison.OrdinalIgnoreCase))
                .Take(12)
                .Select(item => $"{item.BaseName}[{item.Rarity},q{item.Quality}]"));
            Stop($"no unrolled Normal-rarity T16 key in supply tab '{tabName}'" +
                 (observed.Length == 0 ? "" : $"; observed: {observed}"));
            return;
        }
        if (_supplyClickAttempts >= 5)
        {
            Stop($"failed to withdraw a Normal-rarity T16 key after {_supplyClickAttempts} attempts");
            return;
        }
        if (BotMonotonicClock.ElapsedSince(_lastSupplyActionAt).TotalMilliseconds < 600) return;

        var rect = target.Rect.Value;
        var (x, y) = ctx.Snapshot.Window.ToScreen((int)rect.CenterX, (int)rect.CenterY);
        var ticket = ctx.Input.ModifierClick(
            x, y, [VK_LCONTROL], ClickIntent.InteractUi, "withdraw one Normal T16 map key",
            expectResolved: () => _getSnapshot() is { } live
                && CountCarriedTargetMaps(live.Inventory) > 0,
            timeoutMs: 2500);
        if (!ticket.Accepted) return;
        _supplyClickAttempts++;
        _lastSupplyActionAt = BotMonotonicClock.Now;
        _phase = $"map supply: withdrawing {target.BaseName} ({_supplyClickAttempts}/5)";
        Diagnostics.EventLog.Emit(
            "maprun", "maprun.map-supply-withdraw-requested", Diagnostics.EventSeverity.Info,
            _phase, new Dictionary<string, object?>
            {
                ["tab"] = tabName,
                ["baseName"] = target.BaseName,
                ["rarity"] = target.Rarity.ToString(),
                ["quality"] = target.Quality,
            });
    }

    private void TickEnterMapWait(BehaviorContext ctx)
    {
        _lifecyclePhase = MapRunPhase.Entry;
        var transition = _entryTransition.Observe(
            ctx.Snapshot.AreaHash, WorldAreaClassifier.Classify(ctx), AreaTransitionTracker.MonotonicNow());
        if (transition.Outcome == AreaTransitionOutcome.Confirmed)
        {
            ResetSubsystems();
            _deviceFailures = 0;
            _step = Step.Map;
            _phase = "entered map - destination verified";
            MarkMapStart();
            return;
        }
        if (transition.Outcome is AreaTransitionOutcome.UnexpectedDestination or AreaTransitionOutcome.TimedOut)
        {
            Stop($"map entry {transition.Outcome}: expected {transition.ExpectedDestination}, " +
                 $"observed {transition.ObservedRole} at 0x{transition.ObservedAreaHash:X8}");
            return;
        }
        if (transition.Outcome == AreaTransitionOutcome.VerifyingDestination)
        {
            _phase = "entered area - verifying map destination";
            return;
        }

        // Keep driving the device flow (it's clicking/walking into the portal). The area-change
        // tracker flips us to Map once the destination has positive map evidence.
        var dr = _mapDevice.Tick(ctx);
        if (dr == MapDeviceSystem.Result.Failed)
        {
            _deviceFailedAt = BotMonotonicClock.Now;
            _deviceFailures++;
            _step = Step.Hideout;   // fall back; hideout step retries with backoff
            _phase = $"enter-portal failed: {_mapDevice.Status}";
            return;
        }
        _phase = $"entering map: {_mapDevice.Status}";
    }

    private void TickMap(GameSnapshot snapshot, IInputRouter input, BehaviorContext ctx)
    {
        // A checkpoint resurrection or manual emergency exit can return us to hideout while
        // the high-level step still says Map. Never let the clear controller explore/cast in
        // a safe hub and never credit the abandoned map. Re-enter normal hideout preflight;
        // the next device activation will replace any surviving portal set.
        if (IsHideout(ctx))
        {
            ResetSubsystems();
            _needDeposit = ShouldDeposit;
            _step = Step.Hideout;
            _lifecyclePhase = MapRunPhase.Preparation;
            _phase = "returned to hideout before map completion - restarting preflight";
            BubblesBot.Bot.Diagnostics.EventLog.Emit(
                "maprun", "maprun.uncredited-hideout-return",
                BubblesBot.Bot.Diagnostics.EventSeverity.Warning,
                "safe hub observed during map clear; abandoned map was not credited");
            return;
        }

        var maxMapMinutes = _activeStrategy?.Limits.MaxMapMinutes ?? 0;
        if (MapDeadlinePolicy.IsExpired(_mapStartedAt, BotMonotonicClock.Now, maxMapMinutes))
        {
            BeginAbandon(ctx, $"whole-map deadline exceeded ({maxMapMinutes} minutes)", "maprun.map-timeout");
            return;
        }

        _lifecyclePhase = HasUniqueTarget(ctx)
            ? MapRunPhase.BossMechanics
            : MapRunPhase.Clear;
        // Drive the clear tree (exitOnClear=false → it halts on clear instead of taking a
        // transition; combat + loot + flasks all run inside the production map-clear controller).
        _mapFarming.Tick(snapshot, input);
        _phase = $"clearing: {_mapFarming.LastDecision}";

        if (_mapFarming.RequestedAbandonReason is { } recoveryFailure)
        {
            BeginAbandon(ctx, recoveryFailure, "maprun.map-recovery-exhausted");
            return;
        }

        if (_mapFarming.IsCleared)
        {
            _lifecyclePhase = MapRunPhase.Completion;
            _leaveMap.Start(ctx);
            _step = Step.LeaveWait;
            _phase = "cleared — leaving";
        }
    }

    private void TickRecovery(BehaviorContext ctx)
    {
        _lifecyclePhase = MapRunPhase.Entry;
        if (_recoveryStartedAt != TimeSpan.MinValue
            && BotMonotonicClock.Now - _recoveryStartedAt > TimeSpan.FromSeconds(30))
        {
            Stop($"boss checkpoint recovery timed out during {_recoveryLeg}", "died");
            return;
        }

        if (_recoveryLeg == RecoveryLeg.AwaitSafeHub)
        {
            if (!IsHideout(ctx))
            {
                _phase = $"death {_mapDeaths}: waiting for checkpoint resurrection destination";
                return;
            }

            _recoveryLeg = RecoveryLeg.ReturnToMap;
            _recoveryStartedAt = BotMonotonicClock.Now;
            _returnThroughCheckpoint.Reset();
            _phase = $"death {_mapDeaths}: hideout confirmed; locating existing map portal";
            Diagnostics.EventLog.Emit(
                "maprun", "maprun.boss-checkpoint-hideout-confirmed",
                Diagnostics.EventSeverity.Info, _phase);
            return;
        }

        if (_recoveryLeg != RecoveryLeg.ReturnToMap)
        {
            Stop("boss checkpoint recovery entered an invalid state", "died");
            return;
        }

        var result = _returnThroughCheckpoint.Tick(ctx);
        _phase = $"death {_mapDeaths}: entering existing boss checkpoint portal";
        if (result != BehaviorStatus.Success) return;

        if (IsHideout(ctx))
        {
            // EnterAreaTransition normally reports Success only after the area hash changes.
            // Keep this fail-closed guard in case a same-area displacement is ever observed.
            _phase = $"death {_mapDeaths}: portal clicked; awaiting map load";
            return;
        }

        _returnThroughCheckpoint.Reset();
        _recoveryLeg = RecoveryLeg.None;
        _recoveryStartedAt = TimeSpan.MinValue;
        _step = Step.Map;
        _lifecyclePhase = MapRunPhase.BossMechanics;
        _phase = $"death {_mapDeaths}: boss checkpoint re-entered; resuming objectives";
        Diagnostics.EventLog.Emit(
            "maprun", "maprun.boss-checkpoint-recovery-completed",
            Diagnostics.EventSeverity.Info, _phase,
            new Dictionary<string, object?>
            {
                ["deaths"] = _mapDeaths,
                ["areaHash"] = $"0x{ctx.Snapshot.AreaHash:X8}",
            });
    }

    private void TickLeave(BehaviorContext ctx)
    {
        _lifecyclePhase = MapRunPhase.Exit;
        var r = _leaveMap.Tick(ctx);
        _phase = $"leaving: {_leaveMap.Status}";
        if (r == LeaveMapSystem.Result.Failed)
            Stop($"could not leave map: {_leaveMap.Status}");
        else if (r == LeaveMapSystem.Result.Succeeded)
        {
            if (_abandoningMap)
            {
                EmitReport("abandoned", _abandonReason);
                BubblesBot.Bot.Diagnostics.EventLog.Emit(
                    "maprun", "maprun.map-abandoned-timeout",
                    BubblesBot.Bot.Diagnostics.EventSeverity.Warning,
                    $"map abandoned without credit: {_abandonReason}");
            }
            else
            {
                CreditMap();
            }
            ResetSubsystems();
            _abandoningMap = false;
            _abandonReason = "";
            _step = Step.Report;
            _phase = "map attempt reported";
        }
    }

    private void TickReport()
    {
        _lifecyclePhase = MapRunPhase.Report;
        _needDeposit = ShouldDeposit;
        _step = Step.Hideout;
        _phase = $"reported map {_mapsCompleted}/{TargetMaps}; preset={Preset}";
    }

    // ─── Transitions / lifecycle ───────────────────────────────────────────

    private void OnAreaChanged(BehaviorContext ctx)
    {
        switch (_step)
        {
            case Step.EnterMapWait:
                _phase = "area changed - verifying map destination";
                break;
            case Step.LeaveWait:
                // LeaveMapSystem owns destination verification and map credit. Do not reset it
                // merely because some area transition occurred.
                _phase = "area changed - verifying hideout";
                break;
            case Step.Map:
                // Multi-zone maps, side areas, and boss arenas legitimately change the area hash.
                // The nested map-clear controller owns those transitions and does not set
                // IsCleared until its reachable zone graph is exhausted. Never credit here.
                _phase = "map subzone changed - continuing clear";
                break;
            case Step.Recover:
                // Death recovery deliberately crosses map -> hideout -> the same map instance.
                // Its portal behavior owns verification; resetting here would erase the click
                // latch and the preserved boss/Delirium controller state.
                _phase = $"boss checkpoint recovery area change during {_recoveryLeg}";
                break;
            default:
                ResetSubsystems();
                _step = Step.Boot;
                _phase = "area changed — reclassifying";
                break;
        }
    }

    private void CreditMap()
    {
        _mapsCompleted++;
        BubblesBot.Bot.Diagnostics.EventLog.Log("MapRun",
            $"map completed — {_mapsCompleted}/{TargetMaps}");
        EmitReport("completed", "");
    }

    /// <summary>Snapshot loot + start the clock for a new map's run report.</summary>
    private void MarkMapStart()
    {
        _mapStartedUtc = DateTime.UtcNow;
        _mapStartedAt = BotMonotonicClock.Now;
        _abandoningMap = false;
        _abandonReason = "";
        _mapIndex++;
        _mapReportEmitted = false;
        _mapDeaths = 0;
        _recoveryLeg = RecoveryLeg.None;
        _recoveryStartedAt = TimeSpan.MinValue;
        _recoveryOriginAreaHash = 0;
        try
        {
            var loot = _getLoot();
            _lootChaosAtMapStart = loot.TotalChaos;
            _pickupsAtMapStart = loot.Pickups;
        }
        catch { /* loot ledger unavailable — deltas start from 0 */ }
    }

    private void BeginAbandon(BehaviorContext ctx, string reason, string eventType)
    {
        if (_abandoningMap) return;
        _abandoningMap = true;
        _abandonReason = reason;
        _mapFarming.Reset();
        _leaveMap.Start(ctx);
        _step = Step.LeaveWait;
        _lifecyclePhase = MapRunPhase.Exit;
        _phase = $"abandoning: {_abandonReason}";
        BubblesBot.Bot.Diagnostics.EventLog.Emit(
            "maprun", eventType,
            BubblesBot.Bot.Diagnostics.EventSeverity.Warning,
            $"{_abandonReason}; opening a town portal and continuing with the next map",
            new Dictionary<string, object?>
            {
                ["mapIndex"] = _mapIndex,
                ["mapsCompleted"] = _mapsCompleted,
                ["limitMinutes"] = _activeStrategy?.Limits.MaxMapMinutes ?? 0,
            });
    }

    /// <summary>Emit a run report for the just-finished map. Never throws into the tick loop.</summary>
    private void EmitReport(string result, string stopReason)
    {
        if (_mapReportEmitted || _mapIndex <= 0 || _mapStartedAt == TimeSpan.Zero)
            return;

        try
        {
            var loot = _getLoot();
            var strategy = TelemetryStrategy;
            var snap = _getSnapshot();
            _reporter.Report(new Diagnostics.RunReport(
                RunId: $"{_runId}-{_mapIndex}",
                SessionId: _runId,
                Mode: 4,
                ModeName: Name,
                StrategyId: strategy?.Identity.Id ?? "",
                StrategyName: strategy?.Identity.Name ?? "(none)",
                Profile: snap?.Player?.CharacterName ?? "",
                League: snap?.League ?? "",
                MapName: strategy?.Supply.Map.TargetMapName ?? "",
                StartedUtc: _mapStartedUtc,
                EndedUtc: DateTime.UtcNow,
                DurationSec: Math.Max(0, (DateTime.UtcNow - _mapStartedUtc).TotalSeconds),
                Result: result,
                StopReason: stopReason,
                MapIndex: _mapIndex,
                MapsCompleted: _mapsCompleted,
                LootChaos: Math.Max(0, loot.TotalChaos - _lootChaosAtMapStart),
                LootChaosCumulative: loot.TotalChaos,
                ChaosPerHour: loot.ChaosPerHour,
                ItemsPicked: Math.Max(0, loot.Pickups - _pickupsAtMapStart),
                Deaths: _mapDeaths));
            _mapReportEmitted = true;
        }
        catch (Exception ex)
        {
            BubblesBot.Bot.Diagnostics.EventLog.Emit("incident", "run-report.emit-failed",
                Diagnostics.EventSeverity.Warning, $"run report emit failed: {ex.Message}");
        }
    }

    private void ResetSubsystems()
    {
        _movement.Release();
        _mapDevice.Cancel();
        _leaveMap.Cancel();
        _returnThroughCheckpoint.Reset();
        _interact.Cancel();
        _stashDeposit.Cancel();
        _supplyTabSwitcher.Reset();
        _supplyClickAttempts = 0;
        _supplyPanelsExamined.Clear();
        _supplyTierClickAttempts = 0;
        _supplyTabObservedAt = TimeSpan.MinValue;
        _lastSupplyActionAt = TimeSpan.MinValue;
        _mapFarming.Reset();
        _recoveryLeg = RecoveryLeg.None;
        _recoveryStartedAt = TimeSpan.MinValue;
        _recoveryOriginAreaHash = 0;
    }

    private void ResetRun()
    {
        ResetSubsystems();
        _step = Step.Boot;
        _runId = Guid.NewGuid().ToString("N");
        _needDeposit = false;
        _deviceFailedAt = TimeSpan.Zero;
        _deviceFailures = 0;
        _mapsCompleted = 0;
        _itemsStashed = 0;
        _portalScrollsRemaining = 0;
        _mapStartedAt = TimeSpan.Zero;
        _mapDeaths = 0;
        _mapReportEmitted = false;
        _abandoningMap = false;
        _abandonReason = "";
        _stopped = false;
        _stopReason = "";
        _phase = "init";
        _lifecyclePhase = MapRunPhase.Preparation;
        LastDecision = "reset";
        _entryTransition.Reset();
    }

    private void Stop(string reason, string? reportResult = null)
    {
        // A map in progress ended abnormally (death, stuck, device failure) — capture it. Clean
        // stops in hideout (target reached, no strategy) already reported per map via CreditMap.
        if (_step is Step.Map or Step.Recover or Step.LeaveWait)
            EmitReport(reportResult
                ?? (reason.Contains("died", StringComparison.OrdinalIgnoreCase) ? "died" : "stopped"), reason);

        _stopped = true;
        _stopReason = reason;
        _phase = $"STOPPED: {reason}";
        _lifecyclePhase = MapRunPhase.Stopped;
        ResetSubsystems();
        _settings.Mutate(s => s.BotActive = false);   // disarm
        BubblesBot.Bot.Diagnostics.EventLog.Log("MapRun", $"STOPPED + disarmed: {reason}");
        LastDecision = $"STOPPED: {reason}";
    }

    // ─── World classification ──────────────────────────────────────────────

    private static bool IsHideout(BehaviorContext ctx)
        => WorldAreaClassifier.Classify(ctx) == AreaRole.SafeHub;

    private bool ShouldDeposit => _activeStrategy?.Loot.DepositAfterEachMap ?? false;

    private static bool HasUniqueTarget(BehaviorContext ctx)
    {
        if (ctx.Entities is null) return false;
        foreach (var entity in ctx.Entities.Entries.Values)
            if (TargetEligibility.IsEligible(entity) && Threat.RarityRank(entity) >= 3)
                return true;
        return false;
    }
}

/// <summary>Pure whole-map deadline policy. Uses the pause-aware bot clock supplied by the
/// caller, so loading screens and subzone transitions count while a user disarm does not.</summary>
public static class MapDeadlinePolicy
{
    public static bool IsExpired(TimeSpan startedAt, TimeSpan now, int maxMapMinutes)
        => maxMapMinutes > 0
           && startedAt != TimeSpan.Zero
           && now - startedAt >= TimeSpan.FromMinutes(maxMapMinutes);
}
