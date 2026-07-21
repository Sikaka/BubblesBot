using BubblesBot.Bot.Behaviors;
using BubblesBot.Bot.Input;
using BubblesBot.Bot.Modes;
using BubblesBot.Bot.Overlay;
using BubblesBot.Bot.Overlay.Native;
using BubblesBot.Bot.Settings;
using BubblesBot.Bot.Web;
using BubblesBot.Core;
using BubblesBot.Core.Game;
using BubblesBot.Core.Pathfinding;
using BubblesBot.Core.Snapshot;
using System.Diagnostics;

namespace BubblesBot.Bot;

/// <summary>
/// Drives the bot: per-tick snapshot → mode → input gate → render.
/// One instance per process; consumes a pre-bootstrapped MemoryReader and IngameData address.
/// </summary>
public sealed class BotApp : IDisposable, Web.IControlSurface
{
    /// <summary>
    /// Render frequency. Each tick the renderer redraws using the current world snapshot
    /// projected through a live (per-frame) player position. Cheap because expensive reads
    /// (entity list, labels) live in the world snapshot and only refresh at <see cref="WorldHz"/>.
    /// 144 Hz gives smooth player-blip tracking on high-refresh monitors without paying
    /// per-frame entity-walk cost.
    /// </summary>
    private const int TargetHz = 144;

    /// <summary>
    /// World data refresh frequency — entity list, ground labels, terrain pointers. The
    /// expensive memory reads only happen at this cadence; the render path projects the
    /// cached grid coordinates through the live player position so blips track smoothly
    /// even when the underlying entity data is several render frames stale.
    /// </summary>
    private const int WorldHz = 30;

    /// <summary>
    /// Persistent world evidence does not need every 30 Hz perception refresh. Ten samples
    /// per second retains short combat/state transitions while keeping long encounters such
    /// as Simulacrum within the rolling recorder budget. Structured events remain immediate.
    /// </summary>
    private const int FlightHz = 10;

    private readonly ProcessHandle _process;
    private readonly MemoryReader _reader;
    // IngameData is re-resolved from IngameState every tick. PoE replaces the IngameData
    // struct on every area transition; caching the bootstrap address gives stale reads
    // (everything reads as 0) after the first zone change.
    private nint _ingameDataAddress;
    private readonly nint _ingameStateAddress;
    private readonly OverlayWindow _overlayWindow;
    private readonly OverlayRenderer _renderer;
    private readonly InputRouter _input = new();
    private readonly SettingsStore _settings = new();
    private readonly BotEnable _enable;
    private readonly LootMode _loot;
    private readonly OverlayMode _overlay;
    private readonly BlightMode     _blight;
    private readonly SimulacrumRunMode _simulacrum;
    private readonly MapRunMode _mapRun;
    // One shared combat authority (movement + tactical brain) for general-combat modes.
    private readonly Systems.CombatCoordinator _combat;
    private readonly Systems.ReviveSystem _revive = new();
    private readonly ProfileStore _profiles;
    private readonly Strategies.StrategyStore _strategies;
    private readonly WebServer _web;
    private readonly EntityCache _entities;
    // Campaign-guidance worker: its own thread + reader, publishes an immutable GuidanceSnapshot the
    // render path reads lock-free. See Overlay/Navigation/GuidanceWorker.
    private readonly Overlay.Navigation.GuidanceWorker _guidance;
    private readonly Diagnostics.FlightRecorder _flightRecorder = new();
    private object? _publishedStatus;
    private Diagnostics.RuntimeMetricsSnapshot _runtimeMetrics = Diagnostics.RuntimeMetricsSnapshot.Empty;
    private long _tickId;
    private long _lastTickStarted;
    // Not readonly: rebound live when the memory-read league changes (character switched
    // across leagues mid-session). All price consumers share this instance per tick.
    private Core.Knowledge.PriceCatalog _priceCatalog;
    private string _pricingLeague;
    private Task? _priceRefreshTask;
    private Core.Knowledge.PriceCatalog? _priceRefreshCatalog;
    private TimeSpan _nextPriceRefreshAttemptAt = TimeSpan.MinValue;
    private readonly Systems.LootLedger _lootLedger = new();
    private readonly Diagnostics.RunReportStore _runReports = new();
    private readonly Systems.BotRunTimer _runTimer = new();
    private Systems.BotRunTimerState _runTimerState;

    // Game-state gate. Read at render rate (a handful of pointer reads); the world block
    // and mode dispatch only run while the world is live. The chain fails CLOSED: the
    // game-root container is reallocated on every zone change and its global slots sit at
    // NULL for seconds mid-load — Transition is a real not-live signal, not noise. Only
    // GateDisabled (no patterns committed) fails open.
    private readonly GameStateView _gameState;
    private GameStateKind _gameStateKind = GameStateKind.GateDisabled;

    private GameSnapshot? _currentSnapshot;
    // _liveCache is the most recent LivePlayer read, surfaced to modes that want it without
    // having to re-read each tick. Refreshed every render tick.
    private LivePlayer? _liveCache;
    private long _worldRefreshedAt;
    private long _flightRecordedAt;
    private nint _gameHwnd;
    private volatile bool _shutdown;
    private uint _lastAreaHash;
    private int _consecutiveTickFaults;
    private bool? _lastAutomationArmed;

    /// <summary>Signal from the console Ctrl+C handler to exit the tick loop cleanly.</summary>
    public void RequestShutdown() => _shutdown = true;

    // Reused buffer for walking a guidance flow field from the live player each frame.
    private readonly List<PathCell> _guidanceRouteBuf = new();
    // Last frame's drawn guidance routes — for status telemetry only.
    private IReadOnlyList<Overlay.Navigation.GuidanceRoute> _lastGuidanceRoutes = Array.Empty<Overlay.Navigation.GuidanceRoute>();


    public BotApp(ProcessHandle process, MemoryReader reader, nint ingameDataAddress, nint ingameStateAddress,
        IReadOnlyList<nint>? theGameSlots = null)
    {
        _process = process;
        _reader = reader;
        _ingameDataAddress = ingameDataAddress;
        _ingameStateAddress = ingameStateAddress;
        _gameState = new GameStateView(reader, theGameSlots ?? []);
        _overlayWindow = OverlayWindow.Create();
        _renderer = new OverlayRenderer(_overlayWindow);
        _enable = new BotEnable(_settings);
        _loot = new LootMode(() => _currentSnapshot, _settings);
        _overlay = new OverlayMode(_settings, _loot, () => _liveCache, () => _entities, () => _enable.ShouldLoot);
        // Strategy library: seed the two built-ins from the user's live settings on first run,
        // then resolve the per-character active strategy. Constructed before the map-farming
        // mode, which consumes the active strategy each tick.
        _strategies = new Strategies.StrategyStore();
        // Seed from the raw config's legacy map-farm values (captured before any mutation
        // rewrites the file without them). No-op once the library already has strategies.
        var legacyFarm = Strategies.LegacyFarmSettings.LoadFrom(_settings.ConfigPath);
        _strategies.SeedIfEmpty(Strategies.LegacySettingsMigration.BuildSeeds(legacyFarm));
        _blight       = new BlightMode(_settings, () => _currentSnapshot, () => _liveCache, () => _entities);
        _combat       = new Systems.CombatCoordinator(new Systems.MovementSystem(_settings));
        _simulacrum   = new SimulacrumRunMode(_settings, _combat, () => _currentSnapshot, () => _liveCache, () => _entities);
        _mapRun       = new MapRunMode(_settings, _combat, _strategies, () => _currentSnapshot, () => _liveCache, () => _entities,
            _runReports, () => _lootLedger.Snapshot());
        _profiles = new ProfileStore(_settings);
        // The active strategy id is per-character, so it changes when ProfileStore swaps profiles
        // on login. Keep the published active strategy in sync with whatever the current profile
        // points at (and reconcile once now for the profile loaded at construction).
        _settings.Changed += ReconcileActiveStrategy;
        ReconcileActiveStrategy();
        _web = new WebServer(_settings, GetPublishedStatus, this, _strategies, _runReports);
        _entities = new EntityCache(_reader);
        // Campaign-guidance worker — own thread + reader. Enabled from settings; gated to the
        // manual/overlay mode where in-campaign guidance is useful.
        _guidance = new Overlay.Navigation.GuidanceWorker(_process, Core.Campaign.CampaignData.Load(), GuidanceEnabled);
        // Pricing league: read live from ServerData (offset is canary-validated) so items
        // are valued against the CURRENT league's market. The LeagueName setting is only
        // the fallback for when memory isn't readable at attach. The catalog binds at
        // construction — switching leagues mid-session needs a bot restart.
        var league = string.Empty;
        if (reader.TryReadStruct<nint>(ingameDataAddress + KnownOffsets.IngameData.ServerData, out var sdAddr) && sdAddr != 0)
            league = NativeString.Read(reader, sdAddr + KnownOffsets.ServerData.League, maxChars: 64);
        var leagueSource = "memory";
        if (string.IsNullOrWhiteSpace(league)) { league = _settings.Current.LeagueName; leagueSource = "settings fallback"; }
        Diagnostics.EventLog.Log("loot", $"pricing league: '{league}' ({leagueSource})");
        _pricingLeague = league;
        _priceCatalog = new Core.Knowledge.PriceCatalog(league);
        Behaviors.Loot.LootClosestVisible.SharedValueFilter =
            new Behaviors.Loot.ValueFilter(_priceCatalog);
        Behaviors.Loot.LootClosestVisible.SharedLedger = _lootLedger;
        _web.Start();
    }

    private object GetPublishedStatus()
        => Volatile.Read(ref _publishedStatus) ?? new
        {
            connected = false,
            stateLabel = "STARTING",
            runtime = Diagnostics.RuntimeMetricsSnapshot.Empty,
        };

    // ── IControlSurface (web control seam; runs on web threads) ────────────
    // Arming/disarming only touches SettingsStore (thread-safe clone-publish). Input release
    // happens on the tick thread via the existing ShouldAct kill-switch — never call the
    // InputRouter from here.

    private static readonly int[] LegalModes = [0, 4, 5, 6];

    public Web.ControlResult Arm(int? mode)
    {
        if (mode is { } requested)
        {
            var switched = SwitchMode(requested, force: false);
            if (!switched.Ok) return switched;
        }

        var reasons = new List<string>();
        if (!_enable.GateAvailable) reasons.Add("game-state gate unavailable (unsafe startup condition)");
        if (_currentSnapshot is null) reasons.Add("no game snapshot yet — is PoE attached and in-world?");
        else if (_gameStateKind is not (GameStateKind.InGame or GameStateKind.GateDisabled))
            reasons.Add($"game is not in-world (state: {_gameStateKind})");
        // Atlas refuses to run without a valid strategy — it never maps with hidden defaults.
        if (_settings.Current.ActiveMode == 4 && _strategies.Active is null)
            reasons.Add("Atlas needs a strategy — select one on the Atlas tab first");
        if (reasons.Count > 0) return Web.ControlResult.Blocked("disarmed", reasons.ToArray());

        _settings.Mutate(s => s.BotActive = true);
        return _enable.ForegroundOk
            ? Web.ControlResult.Success("armed")
            : Web.ControlResult.Success("armed", "PoE is not the foreground window — the bot acts only while PoE is focused");
    }

    public Web.ControlResult Disarm()
    {
        _settings.Mutate(s => s.BotActive = false);
        return Web.ControlResult.Success("disarmed");
    }

    public Web.ControlResult SwitchMode(int mode, bool force)
    {
        if (!LegalModes.Contains(mode))
            return Web.ControlResult.Blocked(CurrentControlStatus(), $"unknown mode {mode} (legal: {string.Join(", ", LegalModes)})");
        if (_settings.Current.ActiveMode == mode)
            return Web.ControlResult.Success(CurrentControlStatus());
        if (_settings.Current.BotActive && !force)
            return Web.ControlResult.Blocked(CurrentControlStatus(), "bot is armed — disarm first (or pass force to disarm and switch)");

        var warnings = new List<string>();
        if (_settings.Current.BotActive)
        {
            _settings.Mutate(s => s.BotActive = false);
            warnings.Add("bot was disarmed by the forced mode switch; re-arm explicitly");
        }
        _settings.Mutate(s => s.ActiveMode = mode);
        return Web.ControlResult.Success(CurrentControlStatus(), warnings.ToArray());
    }

    public object Meta()
    {
        var snap = _currentSnapshot;
        return new
        {
            botVersion = typeof(BotApp).Assembly.GetName().Version?.ToString() ?? "dev",
            gameAttached = _process.IsAlive && snap is not null,
            gameProcessId = _process.ProcessId,
            gateAvailable = _enable.GateAvailable,
            gameState = _gameStateKind.ToString(),
            foreground = _enable.ForegroundOk,
            armed = _settings.Current.BotActive,
            activeMode = _settings.Current.ActiveMode,
            character = snap?.Player?.CharacterName ?? "",
            profile = _profiles.ActiveProfile,
            league = _pricingLeague,
        };
    }

    private string CurrentControlStatus() => _settings.Current.BotActive ? "armed" : "disarmed";

    /// <summary>Whether campaign guidance should be computed/drawn. Toggle-backed; only meaningful
    /// in the manual/overlay mode (guidance assists a human playing the campaign).</summary>
    private bool GuidanceEnabled()
        => _settings.Current.ShowCampaignGuidance && _settings.Current.ActiveMode == 0;

    /// <summary>
    /// Keep <see cref="Strategies.StrategyStore.Active"/> in sync with the current profile's
    /// per-character <c>ActiveStrategyId</c>. Fires on every settings change (cheap id compare);
    /// only re-activates when the pointer actually moved — e.g. a profile switch on login.
    /// </summary>
    private void ReconcileActiveStrategy()
    {
        var id = _settings.Current.ActiveStrategyId;
        if (string.Equals(id, _strategies.ActiveId, StringComparison.OrdinalIgnoreCase)) return;
        if (string.IsNullOrEmpty(id)) { _strategies.Deactivate(); return; }
        if (!_strategies.Activate(id).Ok) _strategies.Deactivate();
    }

    private (IBehavior Root, string Name, string Decision) ActiveModeSummary()
        => _settings.Current.ActiveMode switch
        {
            4 => (_mapRun.Root,       _mapRun.Name,       _mapRun.LastDecision),
            5 => (_blight.Root,       _blight.Name,       _blight.LastDecision),
            6 => (_simulacrum.Root,   _simulacrum.Name,   _simulacrum.LastDecision),
            _ => (_overlay.Root,      _overlay.Name,      _overlay.LastDecision),
        };

    private string? ActiveRunId() => _settings.Current.ActiveMode switch
    {
        4 => _mapRun.RunId,
        5 when _lastAreaHash != 0 => $"blight-{_lastAreaHash:X8}",
        6 => _simulacrum.RunId,
        _ => null,
    };

    /// <summary>Thin out a cell-by-cell flow-field path to every Nth cell (plus the endpoint) so the
    /// drawn polyline is a handful of segments rather than a dense jagged run of every grid cell.</summary>
    private static IReadOnlyList<PathCell> Decimate(List<PathCell> cells, int step)
    {
        if (cells.Count <= 2) return cells.ToArray();
        var outp = new List<PathCell>(cells.Count / step + 2);
        for (var i = 0; i < cells.Count; i += step) outp.Add(cells[i]);
        if ((cells.Count - 1) % step != 0) outp.Add(cells[^1]);
        return outp;
    }

    /// <summary>Campaign-guidance telemetry for the dashboard: what the worker resolved for the
    /// current area. Lets us confirm the route/target/area-id join without watching the overlay.</summary>
    private object BuildGuidanceStatus()
    {
        var g = _guidance.Current;
        return new
        {
            enabled = GuidanceEnabled(),
            areaId = g.AreaId,
            targets = g.Targets.Select(t => new { t.Label, x = t.Target.X, y = t.Target.Y }).ToArray(),
            drawn = _lastGuidanceRoutes.Select(r => new { r.Label, cells = r.Cells.Count }).ToArray(),
            diagnostic = g.Diagnostic,
        };
    }

    /// <summary>Snapshot of live runtime state shipped to the web UI over WebSocket.</summary>
    private object BuildStatus()
    {
        var snap = _currentSnapshot;
        var player = snap?.Player;

        // Tree + decision come from whichever mode is currently active.
        var (root, modeName, modeDecision) = ActiveModeSummary();
        var treeNodes = TreeSnapshotVisitor.Capture(root);
        var tree = new object[treeNodes.Count];
        for (var i = 0; i < treeNodes.Count; i++)
        {
            var n = treeNodes[i];
            tree[i] = new { depth = n.Depth, name = n.Name, status = n.Status.ToString() };
        }
        // Live skills — bot pushes the user's actual hotbar contents to the dashboard so
        // the skills editor can show one-click "import" entries instead of forcing manual
        // gem-id entry. Each entry: barSlot, gemId, name (when known), readiness.
        object[] liveSkills = Array.Empty<object>();
        if (snap is not null)
        {
            try
            {
                var ls = snap.LiveSkills.Entries;
                liveSkills = ls.Select(e => (object)new
                {
                    barSlot = e.BarSlot,
                    gemId   = e.GemId,
                    name    = e.Name,
                    isReady = e.IsReady,
                    maxUses = e.MaxUses,
                }).ToArray();
            }
            catch { /* loading screen — leave empty */ }
        }

        // Open UI panels — the accidental-click / confirm-open signal, surfaced so live
        // tests can watch menus flip on the dashboard. worldBlocked flags a modal panel
        // covering the game (vendor/dialog/resurrect/popup) — the UiContext gate signal.
        string[] openPanels = Array.Empty<string>();
        var worldBlocked = false;
        if (snap is not null)
        {
            try
            {
                var op = snap.OpenPanels;
                openPanels = op.Open.ToArray();
                worldBlocked = op.IsWorldBlocked();
            }
            catch { /* loading screen — leave empty */ }
        }

        // Tile-map diagnostic — counts + waypoint position + load error if any. Surfaces in
        // the dashboard so we can see what the reader saw without poking memory by hand.
        string tileSummary = "";
        if (snap is not null)
        {
            try
            {
                var tm = snap.TileMap;
                var wp = tm.Find("waypoint");
                tileSummary = $"tiles={tm.TileCount} keys={tm.Find("waypoint").Count} cols={tm.Columns} err='{tm.LoadError}' wp[" +
                              string.Join(",", wp.Select(p => $"({p.X},{p.Y})")) + "]";
            }
            catch (Exception ex) { tileSummary = "tile error: " + ex.Message; }
        }

        // Map-run lifecycle telemetry (only meaningful in mode 4). Mirrors nav/bench so the
        // dashboard + PowerShell polling can drive live testing off /api/status.
        object? loop = null;
        if (_settings.Current.ActiveMode == 4)
        {
            loop = new
            {
                step           = _mapRun.CurrentStep,
                phase          = _mapRun.LoopPhase,
                lifecyclePhase = _mapRun.LifecyclePhase,
                preset         = _mapRun.Preset,
                resourcePolicy = _mapRun.ResourcePolicy,
                mapsCompleted  = _mapRun.MapsCompleted,
                targetMaps     = _mapRun.TargetMaps,
                itemsStashed   = _mapRun.ItemsStashed,
                portalScrolls  = _mapRun.PortalScrollsRemaining,
                stopped        = _mapRun.IsStopped,
                stopReason     = _mapRun.StopReason,
                entryTransition = _mapRun.EntryTransition,
                exitTransition  = _mapRun.ExitTransition,
            };
        }

        object? mechanic = _settings.Current.ActiveMode switch
        {
            5 => new { kind = "blight", state = _blight.PumpTelemetry },
            6 => new { kind = "simulacrum", state = _simulacrum.Telemetry },
            _ => null,
        };

        // Always-on mechanic observations. This is intentionally independent of the active
        // mode/arm bit so overlay-only sessions and incident review can prove why an icon was
        // shown, hidden, clicked, or rejected.
        var mechanics = new MechanicsView(_entities).Entries.Select(m => new
        {
            id = m.Id,
            kind = m.Kind.ToString(),
            // Consumed Eldritch altars never flip Targetable in memory; report the bot's
            // ledger verdict so the dashboard matches what the bot will actually do.
            status = m.Kind == MechanicKind.EldritchAltar && snap is not null
                     && Modes.EldritchAltarLedger.IsResolved(snap.AreaHash, m.Id)
                ? MechanicStatus.Completed.ToString()
                : m.Status.ToString(),
            x = m.GridPosition.X,
            y = m.GridPosition.Y,
            m.Name,
            m.Path,
            stale = m.Entry.IsStale,
            shrineAvailable = m.Entry.ShrineAvailable.Truth.ToString(),
            shrineSource = m.Entry.ShrineAvailable.Source,
            ritualCurrentStateKnown = m.Entry.RitualCurrentState.IsKnown,
            ritualCurrentState = m.Entry.RitualCurrentState.Value,
            ritualInteractionEnabledKnown = m.Entry.RitualInteractionEnabled.IsKnown,
            ritualInteractionEnabled = m.Entry.RitualInteractionEnabled.Value,
        }).ToArray();

        // Always-on Simulacrum contract capture. Available even in Overlay mode so a manual
        // before/start/end wave sequence can be monitored through /api/status and recovered
        // from the flight recording without racing one-shot research commands.
        var simulacrumCapture = _entities.Entries.Values
            .Where(e => e.Path.Contains("Objects/Afflictionator", StringComparison.OrdinalIgnoreCase))
            .Select(e => new
            {
                id = e.Id,
                x = e.GridPosition.X,
                y = e.GridPosition.Y,
                stale = e.IsStale,
                rawStates = e.SimulacrumRawStates,
                active = e.SimulacrumActive,
                goodbye = e.SimulacrumGoodbye,
                wave = e.SimulacrumWave,
                contractValidated = SimulacrumStates.Monolith.IsValidated,
            }).ToArray();

        // Map-clearing telemetry: entity census + frontier progress, built on
        // the tick thread in the mode and swapped in as one reference — safe to read here.
        object? farm = _settings.Current.ActiveMode switch
        {
            4 => _mapRun.MapTelemetry,
            _ => null,
        };

        return new
        {
            connected     = snap is not null,
            loop,
            mechanic,
            mechanics,
            simulacrumCapture,
            farm,
            entityScan    = _entities.LastScanHealth,
            guidance      = BuildGuidanceStatus(),
            flaskBelt     = Core.Game.FlaskBeltReader.Read(_reader, _ingameDataAddress)
                                .Select(s => new { slot = s.Index + 1, kind = s.Kind.ToString(),
                                                   charges = s.Charges, perUse = s.ChargesPerUse, max = s.MaxCharges, canUse = s.CanUse }).ToArray(),
            runtime       = _runtimeMetrics,
            runTimer = new
            {
                running = _runTimerState.Running,
                expired = _runTimerState.Expired,
                elapsedSeconds = _runTimerState.Elapsed.TotalSeconds,
                remainingSeconds = _runTimerState.Remaining?.TotalSeconds,
                limitMinutes = _settings.Current.MaxRunMinutes,
            },
            foreground    = _enable.ForegroundOk,
            gameState     = _gameStateKind.ToString(),
            armed         = _enable.UserEnabled,
            activeMode    = _settings.Current.ActiveMode,
            shouldAct     = _enable.ShouldAct,
            shouldLoot    = _enable.ShouldLoot,
            stateLabel    = _enable.StateLabel,
            lootKeyHeld   = _enable.LootKeyHeld,
            playerHp      = player?.Life.Current ?? 0,
            playerHpMax   = player?.Life.Max ?? 0,
            playerMana    = _liveCache?.ManaCurrent ?? 0,
            playerManaMax = _liveCache?.ManaMax ?? 0,
            playerGridX   = player?.GridPosition.X ?? 0,
            playerGridY   = player?.GridPosition.Y ?? 0,
            areaHash      = snap?.AreaHash ?? 0,
            labelsVisible = snap?.GroundLabels.Count ?? 0,
            openPanels,
            worldBlocked,
            inputState    = _input.GateState,
            mode          = modeName,
            modeDecision,
            runId = ActiveRunId(),
            lootDecision  = _loot.LastDecision,
            lootLedger    = _lootLedger.Snapshot(),
            pricing = new
            {
                league = _pricingLeague,
                entries = _priceCatalog.EntryCount,
                variants = _priceCatalog.VariantCount,
                refreshedAt = _priceCatalog.LastRefreshedAt,
                lastError = _priceCatalog.LastError,
                refreshing = ReferenceEquals(_priceRefreshCatalog, _priceCatalog)
                    && _priceRefreshTask is { IsCompleted: false },
            },
            profile       = _profiles.ActiveProfile,
            character     = player?.CharacterName ?? "",
            league        = snap?.League ?? "",
            tileSummary,
            liveSkills,
            events = Diagnostics.EventLog.Recent(80).Select(e => new {
                seq      = e.Seq,
                t        = e.At.ToString("HH:mm:ss.fff"),
                category = e.Category,
                eventType = e.EventType,
                severity = e.Severity.ToString(),
                message  = e.Message,
            }).ToArray(),
            tree,
        };
    }

    /// <summary>Block on the message pump and tick at <see cref="TargetHz"/> until PoE exits.</summary>
    public void Run()
    {
        var periodTicks = Stopwatch.Frequency / TargetHz;
        var nextTick = Stopwatch.GetTimestamp();
        var nextProcessLivenessCheck = nextTick;
        _gameHwnd = OverlayNative.FindWindowForProcess(_process.ProcessId);

        while (!_shutdown)
        {
            var loopNow = Stopwatch.GetTimestamp();
            if (loopNow >= nextProcessLivenessCheck)
            {
                nextProcessLivenessCheck = loopNow + Stopwatch.Frequency;
                if (!_process.IsAlive)
                {
                    Diagnostics.EventLog.Emit(
                        "process", "process.target-exited", Diagnostics.EventSeverity.Critical,
                        $"attached PoE process {_process.ProcessId} exited; restart BubblesBot to attach to the new client");
                    Console.Error.WriteLine($"Attached PoE process {_process.ProcessId} exited. Restart BubblesBot after PoE is running.");
                    break;
                }
            }

            if (_gameHwnd != 0 && !OverlayNative.IsWindowForProcess(_gameHwnd, _process.ProcessId))
                _gameHwnd = 0;
            if (_gameHwnd == 0)
                _gameHwnd = OverlayNative.FindWindowForProcess(_process.ProcessId);
            if (_gameHwnd != 0)
                _overlayWindow.TrackGameWindow(_gameHwnd);

            if (!_overlayWindow.PumpMessages()) break;

            try
            {
                Tick();
                _consecutiveTickFaults = 0;
            }
            catch (Exception ex)
            {
                // A behavior bug must never leave held input latched or preserve an armed
                // profile. Keep the process alive so the dashboard and rolling recorder can
                // expose the fault; only terminate cleanly if the entire tick path repeatedly
                // fails even after automation has been disarmed.
                _consecutiveTickFaults++;
                _input.CancelAll();
                if (_settings.Current.BotActive)
                    _settings.Mutate(settings => settings.BotActive = false);
                Diagnostics.EventLog.Emit(
                    "incident", "incident.tick-exception", Diagnostics.EventSeverity.Critical,
                    $"tick exception contained and automation disarmed: {ex.GetType().Name}: {ex.Message}",
                    new Dictionary<string, object?>
                    {
                        ["exceptionType"] = ex.GetType().FullName,
                        ["message"] = ex.Message,
                        ["stackTrace"] = ex.StackTrace,
                        ["consecutiveFaults"] = _consecutiveTickFaults,
                    });

                if (_consecutiveTickFaults >= 5)
                {
                    Diagnostics.EventLog.Emit(
                        "incident", "incident.tick-circuit-open", Diagnostics.EventSeverity.Critical,
                        "five consecutive tick failures; shutting down cleanly after preserving diagnostics");
                    _shutdown = true;
                }
            }

            nextTick += periodTicks;
            var remaining = nextTick - Stopwatch.GetTimestamp();
            if (remaining > 0)
            {
                var remainingMs = remaining * 1000.0 / Stopwatch.Frequency;
                if (remainingMs > 1.5) Thread.Sleep((int)(remainingMs - 0.5));
                while (Stopwatch.GetTimestamp() < nextTick) Thread.Yield();
            }
            else if (-remaining > periodTicks * 4)
            {
                // A long world/pathfinding frame should not cause a burst of catch-up ticks.
                nextTick = Stopwatch.GetTimestamp();
            }
        }
    }

    private void Tick()
    {
        var tickStarted = Stopwatch.GetTimestamp();
        var tickIntervalMs = _lastTickStarted == 0
            ? 0
            : Stopwatch.GetElapsedTime(_lastTickStarted, tickStarted).TotalMilliseconds;
        _lastTickStarted = tickStarted;
        var tickId = Interlocked.Increment(ref _tickId);
        Diagnostics.EventLog.SetContext(tickId, _lastAreaHash, ActiveModeSummary().Name, ActiveRunId());
        var readsBefore = _reader.ReadCount;
        var bytesBefore = _reader.BytesRead;
        var failuresBefore = _reader.FailedReads;
        var worldDurationMs = 0.0;
        var entityDurationMs = 0.0;

        var window = new WindowInfo(
            _overlayWindow.OriginX, _overlayWindow.OriginY,
            _overlayWindow.Width,   _overlayWindow.Height);

        // ── Always-fresh per-render reads ──────────────────────────────────
        // PoE replaces IngameData on area transitions; one pointer hop keeps us aligned.
        if (_reader.TryReadStruct<nint>(_ingameStateAddress + KnownOffsets.IngameState.Data, out var liveDataAddr)
            && liveDataAddr != 0)
        {
            _ingameDataAddress = liveDataAddr;
        }

        // Live player position — read EVERY render frame for smooth blip tracking.
        // Independent of the world snapshot, which only refreshes at WorldHz.
        var live = LivePlayer.TryRead(_reader, _ingameDataAddress);
        _liveCache = live;

        // Game-state gate: classify PoE's top-level state every frame. Leaving InGame
        // (loading screen, login, character select) cancels all inputs immediately —
        // otherwise a held walk key stays down through the entire load.
        var kind = _gameState.ReadKind();
        var gameStateChanged = kind != _gameStateKind;
        if (gameStateChanged)
        {
            Diagnostics.EventLog.Log("gate", $"game state: {_gameStateKind} -> {kind}");
            if (kind is not (GameStateKind.InGame or GameStateKind.GateDisabled))
                _input.CancelAll();
            _gameStateKind = kind;
        }
        var worldLive = kind is GameStateKind.InGame or GameStateKind.GateDisabled;

        // Hotkeys + input gate are cheap, run every frame so pressing the loot key feels
        // instant rather than waiting up to 33 ms for the next world tick.
        _enable.Tick(_gameHwnd,
            gateAvailable: kind != GameStateKind.GateDisabled,
            gameStateAllowsInput: kind == GameStateKind.InGame);
        Systems.BotMonotonicClock.SetPaused(!_enable.ShouldAct);
        var automationArmed = _settings.Current.BotActive;
        if (_lastAutomationArmed == true && !automationArmed
            && _settings.Current.ActiveMode == 6)
        {
            // A user pause deliberately gives the current wave a fresh configured budget on
            // resume. BotMonotonicClock already excludes the paused duration; this also avoids
            // carrying pre-pause elapsed time into a later troubleshooting/restart session.
            _simulacrum.RestartActiveWaveClock();
        }
        _lastAutomationArmed = automationArmed;
        _runTimerState = _runTimer.Observe(
            automationArmed,
            _settings.Current.MaxRunMinutes,
            Systems.BotMonotonicClock.RawNow);
        if (_runTimerState.Expired && _settings.Current.BotActive)
        {
            var elapsed = _runTimerState.Elapsed;
            _settings.Mutate(settings => settings.BotActive = false);
            _input.CancelAll();
            Diagnostics.EventLog.Emit(
                "automation", "automation.max-runtime-reached",
                Diagnostics.EventSeverity.Warning,
                $"maximum run time reached after {elapsed.TotalMinutes:F1} minutes; automation disarmed",
                new Dictionary<string, object?>
                {
                    ["elapsedSeconds"] = elapsed.TotalSeconds,
                    ["limitMinutes"] = _settings.Current.MaxRunMinutes,
                });
        }
        _settings.Tick();
        TickPriceRefresh();
        _input.Tick();
        // Kill-switch: any time the bot shouldn't be acting (toggle off, PoE not foreground)
        // we drop the click in flight AND release every held key. Without the held-key half,
        // alt-tabbing while loot key is held would leave the key stuck down in PoE's view.
        if (!_enable.ShouldAct) _input.CancelAll();

        // ── World-rate refresh (entity list, labels, terrain, mode logic) ──
        // Decoupled from the render rate so 1000+ entities don't get walked at 144 Hz.
        // Skipped entirely while the world isn't live: snapshot/entity reads during a
        // loading screen return garbage or stale data, and modes must not act on it. The
        // previous snapshot is kept for the renderer; the AreaHash flip on re-entry
        // triggers the usual mode/cache resets.
        var now = Stopwatch.GetTimestamp();
        var worldDue = worldLive
                    && (_currentSnapshot is null
                        || _worldRefreshedAt == 0
                        || Stopwatch.GetElapsedTime(_worldRefreshedAt, now).TotalMilliseconds >= 1000.0 / WorldHz);
        if (worldDue)
        {
            var worldStarted = Stopwatch.GetTimestamp();
            _currentSnapshot = new GameSnapshot(_reader, _ingameDataAddress, _ingameStateAddress, window);
            _worldRefreshedAt = now;

            // Switch profiles before any mode sees this tick. SettingsStore atomically
            // publishes the new profile, so all systems observe one coherent version.
            if (_currentSnapshot.Player is { } profilePlayer
                && !string.IsNullOrEmpty(profilePlayer.CharacterName))
            {
                _profiles.SwitchTo(profilePlayer.CharacterName);
            }

            // Area-change side effects (cancel input, reset modes). Hash-based detection;
            // additional in-transition signals live in the renderer's IsInTransition() check.
            var areaHash = _currentSnapshot.AreaHash;
            if (areaHash != 0 && areaHash != _lastAreaHash)
            {
                var previousAreaHash = _lastAreaHash;
                _lastAreaHash = areaHash;
                Diagnostics.EventLog.SetContext(tickId, areaHash, ActiveModeSummary().Name, ActiveRunId());
                if (previousAreaHash != 0)
                {
                    _input.OnAreaChanged();
                    _overlay.Reset();
                    _blight.Reset();
                    // MapRunMode spans area transitions and resets only per-area subsystems.
                    _entities.Clear();   // entity IDs may collide across instances; start fresh
                }
            }

            // Refresh the entity cache. Component addresses are cached per-entity for the
            // entity's lifetime, so the per-tick cost is just N small struct reads (HP,
            // position, IsMoving) instead of N component-map walks.
            if (_reader.TryReadStruct<nint>(_ingameDataAddress + KnownOffsets.IngameData.EntityList, out var entityListAddr)
                && entityListAddr != 0)
            {
                var entityStarted = Stopwatch.GetTimestamp();
                _entities.Refresh(entityListAddr, live?.GridPosition ?? default);
                entityDurationMs = Stopwatch.GetElapsedTime(entityStarted).TotalMilliseconds;
            }

            // Mode dispatch — Loot is the only mode that requires holding the loot key.
            // All other modes run on ShouldAct alone.
            if (_enable.ShouldAct)
            {
                // Death gate — runs ABOVE mode dispatch. While the resurrect panel is up we click
                // "Resurrect at Checkpoint" and skip the mode; on the revive edge we stop the bot
                // (land safely in hideout). Resume-into-map is the mapping loop's job, not built here.
                var revive = _revive.Tick(_currentSnapshot, _input);
                if (revive == Systems.ReviveSystem.Result.JustRevived)
                {
                    var simulacrumRecovery = _settings.Current.ActiveMode == 6
                        && _simulacrum.NotifyRevived();
                    if (simulacrumRecovery)
                    {
                        Diagnostics.EventLog.Log("revive",
                            $"revived at checkpoint (death #{_revive.Deaths}); Simulacrum recovery retained arm");
                    }
                    else
                    {
                        _settings.Mutate(s => s.BotActive = false);
                        Diagnostics.EventLog.Log("revive",
                            $"revived at checkpoint (death #{_revive.Deaths}); bot stopped");
                    }
                }
                else if (revive == Systems.ReviveSystem.Result.Reviving)
                {
                    // dead / clicking — don't dispatch a mode into a corpse
                }
                else
                switch (_settings.Current.ActiveMode)
                {
                    case 4: _mapRun.Tick(_currentSnapshot, _input); break;
                    case 5: _blight.Tick(_currentSnapshot, _input); break;
                    case 6: _simulacrum.Tick(_currentSnapshot, _input); break;
                    case 0:
                    default:
                        _overlay.Tick(_currentSnapshot, _input);
                        break;
                }
            }

            // League rebind — a character swap can land in a different league; pricing must
            // follow the memory-read league, not the one from attach time.
            var liveLeague = _currentSnapshot?.League;
            if (!string.IsNullOrWhiteSpace(liveLeague) && liveLeague != _pricingLeague)
            {
                Diagnostics.EventLog.Log("loot",
                    $"league changed '{_pricingLeague}' -> '{liveLeague}' — rebinding price catalog");
                _pricingLeague = liveLeague;
                _priceCatalog = new Core.Knowledge.PriceCatalog(liveLeague);
                _nextPriceRefreshAttemptAt = TimeSpan.MinValue;
                Behaviors.Loot.LootClosestVisible.SharedValueFilter =
                    new Behaviors.Loot.ValueFilter(_priceCatalog);
            }
            // Hand the guidance worker a fresh cursor. It re-resolves off-thread on area change and
            // publishes a GuidanceSnapshot the render path reads lock-free.
            _guidance.Publish(new Overlay.Navigation.WorldCursor(
                _ingameDataAddress, _currentSnapshot!.AreaHash, live?.GridPosition ?? default));

            worldDurationMs = Stopwatch.GetElapsedTime(worldStarted).TotalMilliseconds;
        }

        // ── Render every tick ──────────────────────────────────────────────
        // HUD lines come from whichever map-clearing mode is active; they persist between
        // world ticks (rebuilt at 30 Hz, rendered at 144 Hz).
        IReadOnlyList<string>? modeHud = _settings.Current.ActiveMode switch
        {
            4 => _mapRun.HudLines,
            5 => _blight.HudLines,
            6 => _simulacrum.HudLines,
            _ => null,
        };
        var profit = _lootLedger.OverlaySummary();
        var hud = new List<string>((modeHud?.Count ?? 0) + 1);
        if (modeHud is not null) hud.AddRange(modeHud);
        hud.Add($"Profit: {profit.TotalChaos:F1}c | {profit.ChaosPerHour:F1}c/h | {profit.Pickups} pickups");

        // Campaign guidance: read the worker's latest snapshot lock-free, then re-walk each target's
        // flow field from the LIVE player cell this frame (cheap, O(path)) so routes track smoothly.
        var guidanceSnap = GuidanceEnabled() ? _guidance.Current : null;
        List<Overlay.Navigation.GuidanceRoute>? guidanceRoutes = null;
        if (guidanceSnap is { Targets.Count: > 0 } gs && live is { } glp)
        {
            var playerCell = new Vector2i { X = glp.GridPosition.X, Y = glp.GridPosition.Y };
            guidanceRoutes = new List<Overlay.Navigation.GuidanceRoute>(gs.Targets.Count);
            foreach (var t in gs.Targets)
            {
                if (!t.Field.TryGetPath(playerCell, _guidanceRouteBuf) || _guidanceRouteBuf.Count == 0) continue;
                guidanceRoutes.Add(new Overlay.Navigation.GuidanceRoute(
                    t.Label, t.Kind, Decimate(_guidanceRouteBuf, 6), t.Target));
            }
        }
        _lastGuidanceRoutes = guidanceRoutes ?? (IReadOnlyList<Overlay.Navigation.GuidanceRoute>)Array.Empty<Overlay.Navigation.GuidanceRoute>();

        var ctx = new RenderContext(
            Snapshot:       _currentSnapshot,
            Input:          _input,
            Loot:           _loot,
            Status:         window.IsValid ? string.Empty : "no window",
            Enable:         _enable,
            Live:           live,
            Entities:       _entities,
            Prices:         _priceCatalog,
            Hud:            hud,
            Guidance:       guidanceSnap,
            GuidanceRoutes: guidanceRoutes,
            HpBars:         _settings.Current.ShowEntityHpBars,
            PlayerBlip:     _settings.Current.ShowMapPlayerBlip);
        _renderer.Render(ctx);

        var tickDurationMs = Stopwatch.GetElapsedTime(tickStarted).TotalMilliseconds;
        _runtimeMetrics = new Diagnostics.RuntimeMetricsSnapshot(
            tickId,
            Stopwatch.GetTimestamp(),
            tickIntervalMs,
            tickDurationMs,
            worldDurationMs,
            entityDurationMs,
            worldDue,
            tickDurationMs > 1000.0 / TargetHz,
            _reader.ReadCount - readsBefore,
            _reader.BytesRead - bytesBefore,
            _reader.FailedReads - failuresBefore,
            _reader.ReadCount,
            _reader.BytesRead,
            _reader.FailedReads,
            _settings.Version);

        // Materialize status after metrics are finalized so all fields describe this tick,
        // not a mixture of the current world and the previous runtime sample.
        object? nextStatus = null;
        if (worldDue || gameStateChanged || Volatile.Read(ref _publishedStatus) is null)
            nextStatus = BuildStatus();

        var flightDue = worldDue && (_flightRecordedAt == 0
            || Stopwatch.GetElapsedTime(_flightRecordedAt).TotalMilliseconds >= 1000.0 / FlightHz);
        if (flightDue && _currentSnapshot is not null)
        {
            _flightRecordedAt = Stopwatch.GetTimestamp();
            var (modeRoot, modeName, modeDecision) = ActiveModeSummary();
            _ = modeRoot;
            var player = _currentSnapshot.Player;
            var recordedEntities = _entities.Entries.Values.Select(e => new Diagnostics.FlightEntity(
                e.Id,
                e.Path,
                e.GridPosition.X,
                e.GridPosition.Y,
                e.HpCurrent,
                e.HpMax,
                e.Targetability.Truth,
                e.AlliedReaction.Truth,
                e.Dormancy.Truth,
                e.LifeReadable.Truth,
                e.IsMoving,
                e.IsStale,
                e.Kind,
                e.Disposition,
                e.Tier,
                e.ShrineAvailable.Truth,
                e.RitualCurrentState.IsKnown,
                e.RitualCurrentState.Value,
                e.RitualInteractionEnabled.IsKnown,
                e.RitualInteractionEnabled.Value,
                e.SimulacrumRawStates,
                e.SimulacrumActive.IsKnown,
                e.SimulacrumActive.Value,
                e.SimulacrumGoodbye.IsKnown,
                e.SimulacrumGoodbye.Value,
                e.SimulacrumWave.IsKnown,
                e.SimulacrumWave.Value)).ToArray();
            _flightRecorder.RecordWorldFrame(new Diagnostics.WorldRecordFrame(
                tickId,
                Stopwatch.GetTimestamp(),
                ActiveRunId(),
                _lastAreaHash,
                _gameStateKind.ToString(),
                _settings.Current.ActiveMode,
                modeName,
                modeDecision,
                player is null ? null : new Diagnostics.FlightPlayer(
                    player.GridPosition.X,
                    player.GridPosition.Y,
                    player.Life.Current,
                    player.Life.Max),
                _entities.LastScanHealth,
                _runtimeMetrics,
                recordedEntities));
        }

        if (nextStatus is not null)
            Volatile.Write(ref _publishedStatus, nextStatus);
    }

    private void TickPriceRefresh()
    {
        var now = Systems.BotMonotonicClock.Now;
        if (_priceRefreshTask is not null)
        {
            if (!_priceRefreshTask.IsCompleted) return;
            try { _priceRefreshTask.GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                Diagnostics.EventLog.Emit("loot", "loot.prices-refresh-fault",
                    Diagnostics.EventSeverity.Error, ex.Message);
            }

            if (ReferenceEquals(_priceRefreshCatalog, _priceCatalog))
            {
                if (string.IsNullOrEmpty(_priceCatalog.LastError))
                {
                    Diagnostics.EventLog.Emit("loot", "loot.prices-refreshed",
                        Diagnostics.EventSeverity.Info,
                        $"poe.ninja: {_priceCatalog.EntryCount} names / {_priceCatalog.VariantCount} variants ({_pricingLeague})");
                    _nextPriceRefreshAttemptAt = now + TimeSpan.FromSeconds(30);
                }
                else
                {
                    Diagnostics.EventLog.Emit("loot", "loot.prices-refresh-failed",
                        Diagnostics.EventSeverity.Warning, _priceCatalog.LastError);
                    _nextPriceRefreshAttemptAt = now + TimeSpan.FromMinutes(5);
                }
            }
            _priceRefreshTask = null;
            _priceRefreshCatalog = null;
        }

        if (now < _nextPriceRefreshAttemptAt || !_priceCatalog.NeedsRefresh) return;
        _priceRefreshCatalog = _priceCatalog;
        _priceRefreshTask = _priceCatalog.RefreshAsync();
        Diagnostics.EventLog.Emit("loot", "loot.prices-refresh-started",
            Diagnostics.EventSeverity.Info, $"refreshing poe.ninja prices for {_pricingLeague}");
    }

    public void Dispose()
    {
        _guidance.Dispose();
        _web.Dispose();
        _flightRecorder.Dispose();
        _renderer.Dispose();
        _overlayWindow.Dispose();
    }
}
