using BubblesBot.Bot.Behaviors;
using BubblesBot.Bot.Behaviors.Interact;
using BubblesBot.Bot.Input;
using BubblesBot.Bot.Settings;
using BubblesBot.Bot.Strategies;
using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Modes;

/// <summary>
/// End-to-end Shaper Guardian / Maven Formed farm. Witness state is rescanned before every
/// objective, so restarts can attach to a partially completed rotation without guessing.
/// </summary>
public sealed class GuardianRotaRunMode : IBotMode
{
    private enum Step
    {
        Boot, PrepareMaps, ScanAtlas, Decide, GuardianDevice, GuardianMap,
        OpenKirac, RollInvitation, ActivateInvitation, FormedArena,
        LeaveEncounter, DepositLoot, Recover, Stopped,
    }

    private const string KiracPath = "Metadata/NPC/Epilogue/KiracHideout";
    private const string PodiumPath = "Metadata/Terrain/EndGame/MapAtlasMaven/Objects/MavenBossRushObject";
    private const string FormedOption = "Invitation: The Formed";
    private const int AtlasKeyVk = 0x47;
    private const int InventoryKeyVk = 0x49;
    private const int EscapeKeyVk = 0x1B;

    private readonly SettingsStore _settings;
    private readonly Func<GameSnapshot?> _getSnapshot;
    private readonly Func<LivePlayer?> _getLive;
    private readonly Func<EntityCache?> _getEntities;
    private readonly CombatCoordinator _combat;
    private readonly GuardianAtlasScanner _scanner;
    private readonly GuardianItemRollController _roller;
    private readonly MapDeviceSystem _device;
    private readonly LeaveMapSystem _leave;
    private readonly StashDepositSystem _deposit;
    private readonly InteractSystem _interact = new();
    private readonly InteractWorldEntity _openKirac;
    private readonly InteractWorldEntity _startPodium;
    private readonly EnterAreaTransition _formedPortal;
    private readonly EnterAreaTransition _recoveryPortal;
    private readonly HashSet<uint> _preInvitationPortals = [];
    private readonly PushCombatMode _guardianCombat;
    private readonly PushCombatMode _formedCombat;
    private readonly GuardianRotaRecoveryStore _recoveryStore = new();

    private Step _step = Step.Boot;
    private Step _resumeStep;
    private GuardianRotaProgress? _progress;
    private GuardianRotaObjective _objective;
    private FarmingStrategy? _activeStrategy;
    private TimeSpan _stepStartedAt = BotMonotonicClock.Now;
    private TimeSpan _formedCompleteAt = TimeSpan.MinValue;
    private bool _formedActivateClicked;
    private bool _recoveryAllowed;
    private uint _recoveryAreaHash;
    private bool _recoveryTransitionRequested;
    private bool _recoverySawHub;
    private Vector2i? _lastPersistedTraversalOrigin;
    private bool _encounterCompleteCertified;
    private TimeSpan _bootHydrationStartedAt = TimeSpan.MinValue;
    private bool _stopped;
    private string _stopReason = string.Empty;
    private readonly string _runId = Guid.NewGuid().ToString("N");

    public string Name => "Guardian Rota";
    public IBehavior Root => _objective.Kind == GuardianRotaObjectiveKind.FormedInvitation
        ? _formedCombat.Root : _guardianCombat.Root;
    public string RunId => _runId;
    public string LastDecision { get; private set; } = "init";
    public bool IsStopped => _stopped;
    public string StopReason => _stopReason;
    public IReadOnlyList<string> HudLines =>
    [
        $"Guardian Rota: {_step} | rotation {Math.Min((_progress?.RotationsCompleted ?? 0) + 1, _settings.Current.GuardianTargetRotations)}/{_settings.Current.GuardianTargetRotations}",
        $"Objective: {_objective.Name} | maps {_progress?.GuardianMapsCompleted ?? 0} | invitations {_progress?.InvitationsCompleted ?? 0}",
        $"Portals: {_progress?.PortalEntriesThisEncounter ?? 0}/6 | deaths {_progress?.DeathsThisEncounter ?? 0}/5",
        LastDecision,
    ];
    public object Telemetry => new
    {
        lifecycleStep = _step.ToString(),
        objective = _objective,
        rotationsCompleted = _progress?.RotationsCompleted ?? 0,
        targetRotations = _settings.Current.GuardianTargetRotations,
        guardianMapsCompleted = _progress?.GuardianMapsCompleted ?? 0,
        invitationsCompleted = _progress?.InvitationsCompleted ?? 0,
        portalEntries = _progress?.PortalEntriesThisEncounter ?? 0,
        deaths = _progress?.DeathsThisEncounter ?? 0,
        atlas = _scanner.Status,
        roller = _roller.Status,
        device = _device.Status,
        combat = _objective.Kind == GuardianRotaObjectiveKind.FormedInvitation
            ? _formedCombat.Telemetry : _guardianCombat.Telemetry,
        stopped = _stopped,
        stopReason = _stopReason,
    };

    public GuardianRotaRunMode(
        SettingsStore settings, CombatCoordinator combat,
        Func<GameSnapshot?> getSnapshot, Func<LivePlayer?> getLive,
        Func<EntityCache?> getEntities)
    {
        _settings = settings;
        _combat = combat;
        _getSnapshot = getSnapshot;
        _getLive = getLive;
        _getEntities = getEntities;
        _scanner = new GuardianAtlasScanner(getSnapshot);
        _roller = new GuardianItemRollController(
            getSnapshot, () => _settings.Current.MapModifiers.PolicyOverrides);
        _device = new MapDeviceSystem(combat.Movement, combat.Skills, getSnapshot, getLive, getEntities);
        _leave = new LeaveMapSystem(combat.Movement,
            () => _settings.Current.StackedDeckPortalKeyVk,
            ctx => WorldAreaClassifier.Classify(ctx) == AreaRole.SafeHub,
            combat.Skills,
            getSnapshot);
        _deposit = new StashDepositSystem(
            combat.Movement, combat.Skills, getSnapshot,
            retainItem: (_, item) => IsGuardianSupply(item));
        _openKirac = new InteractWorldEntity(
            "open Commander Kirac", _interact, combat.Movement, combat.Skills,
            ctx => FindByPath(ctx, KiracPath),
            (ctx, _) => NpcDialogView.Read(ctx.Snapshot.Reader, ctx.Snapshot.IngameStateAddress)
                .FindExact(FormedOption).Count == 1);
        _startPodium = new InteractWorldEntity(
            "start The Formed podium", _interact, combat.Movement, combat.Skills,
            ctx => FindByPath(ctx, PodiumPath),
            (ctx, entry) => ReadPodiumPhase(ctx.Snapshot, entry)
                is MavenInvitationStates.BossRushObject.EncounterActive
                or MavenInvitationStates.BossRushObject.EncounterComplete,
            retryUntilActivated: true);
        _formedPortal = new EnterAreaTransition(
            "enter fresh The Formed portal", _interact, combat.Movement, combat.Skills,
            getSnapshot,
            entity => IsPortal(entity) && !_preInvitationPortals.Contains(entity.Id));
        _recoveryPortal = new EnterAreaTransition(
            "re-enter Guardian encounter", _interact, combat.Movement, combat.Skills,
            getSnapshot, IsPortal);
        _guardianCombat = new PushCombatMode(
            settings, combat, getSnapshot, getLive, getEntities,
            orchestrated: true, getStrategy: () => _activeStrategy);
        _formedCombat = new PushCombatMode(
            settings, combat, getSnapshot, getLive, getEntities,
            orchestrated: true, getStrategy: () => _activeStrategy);
    }

    public void Tick(GameSnapshot snapshot, IInputRouter input)
    {
        if (_stopped) return;
        _progress ??= new GuardianRotaProgress(_settings.Current.GuardianTargetRotations);
        var ctx = new BehaviorContext(
            snapshot, input, _settings.Current, _getLive(), _getEntities(), _activeStrategy);

        switch (_step)
        {
            case Step.Boot: TickBoot(ctx); break;
            case Step.PrepareMaps: TickPrepareMaps(ctx); break;
            case Step.ScanAtlas: TickScanAtlas(ctx); break;
            case Step.Decide: TickDecide(ctx); break;
            case Step.GuardianDevice: TickGuardianDevice(ctx); break;
            case Step.GuardianMap: TickGuardianMap(snapshot, input, ctx); break;
            case Step.OpenKirac: TickOpenKirac(ctx); break;
            case Step.RollInvitation: TickRollInvitation(ctx); break;
            case Step.ActivateInvitation: TickActivateInvitation(ctx); break;
            case Step.FormedArena: TickFormedArena(snapshot, input, ctx); break;
            case Step.LeaveEncounter: TickLeave(ctx); break;
            case Step.DepositLoot: TickDeposit(ctx); break;
            case Step.Recover: TickRecover(ctx); break;
        }
    }

    private void TickBoot(BehaviorContext ctx)
    {
        var role = WorldAreaClassifier.Classify(ctx);
        var saved = _recoveryStore.Load();
        if (role != AreaRole.SafeHub)
        {
            if (saved is null)
            {
                Stop("Guardian Rota was armed outside the hideout with no active encounter checkpoint");
                return;
            }
            RestoreRecoveryState(saved);
            if (saved.EncounterComplete)
            {
                _encounterCompleteCertified = true;
                // Version-0 checkpoints predate durable totals and therefore still need the
                // certified encounter applied on recovery. Version-1 totals were persisted
                // after Record*Clear and must not be incremented a second time.
                if (saved.ProgressVersion == 0)
                {
                    if (saved.Kind == GuardianRotaObjectiveKind.FormedInvitation)
                        _progress!.RecordInvitationClear();
                    else
                        _progress!.RecordGuardianClear();
                }
                StartLeave(ctx, $"resumed certified-complete {saved.ObjectiveName}; leaving encounter");
                return;
            }
            if (saved.Kind == GuardianRotaObjectiveKind.FormedInvitation)
                _formedCombat.PrepareForSameInstanceCheckpointRecovery();
            else
                _guardianCombat.PrepareForSameInstanceCheckpointRecovery();
            Advance(saved.Kind == GuardianRotaObjectiveKind.FormedInvitation
                ? Step.FormedArena : Step.GuardianMap,
                $"resumed active {saved.ObjectiveName} after restart");
            return;
        }
        if (_bootHydrationStartedAt == TimeSpan.MinValue)
        {
            _bootHydrationStartedAt = BotMonotonicClock.Now;
            LastDecision = "waiting for hideout portal labels to hydrate";
            return;
        }
        // A certified completion is persisted before the character leaves the encounter. If the
        // process is rebuilt after reaching the hideout, resume the post-completion work instead
        // of re-entering a stale portal or forgetting the durable campaign totals.
        if (saved is { EncounterComplete: true })
        {
            RestoreRecoveryState(saved);
            if (saved.ProgressVersion == 0)
            {
                if (saved.Kind == GuardianRotaObjectiveKind.FormedInvitation)
                    _progress!.RecordInvitationClear();
                else
                    _progress!.RecordGuardianClear();
            }
            _progress!.ResetEncounterBudget();
            if (saved.Kind == GuardianRotaObjectiveKind.FormedInvitation)
            {
                _deposit.Start(_settings.Current.GuardianDumpTabName);
                Advance(Step.DepositLoot,
                    $"resumed completed {saved.ObjectiveName}; depositing rotation loot");
            }
            else
            {
                _scanner.Start();
                Advance(Step.ScanAtlas,
                    $"resumed completed {saved.ObjectiveName}; refreshing witness state");
            }
            return;
        }
        // Preserve an already-open Guardian map across a bot rebuild/restart. Portal labels
        // carry the exact destination name, so this does not guess from an instance hash
        // and does not consume a replacement key.
        var openGuardian = ctx.Snapshot.GroundLabels
            .Where(label => label.IsLabelVisible && label.IsRectOnScreen
                && label.Path.Contains("Portal", StringComparison.OrdinalIgnoreCase))
            .Select(label => label.DisplayName)
            .FirstOrDefault(name => GuardianRotationPolicy.Maps.Contains(
                name, StringComparer.OrdinalIgnoreCase));
        // A completed map can leave unused portals behind while the persisted objective has
        // already advanced to the next Guardian. Never let those stale portals override the
        // newer saved objective after a rebuild/restart.
        if (!string.IsNullOrEmpty(openGuardian)
            && saved is not null
            && !saved.ObjectiveName.Equals(openGuardian, StringComparison.OrdinalIgnoreCase))
            openGuardian = null;
        if (!string.IsNullOrEmpty(openGuardian))
        {
            var remainingPortals = ctx.Snapshot.GroundLabels.Count(label =>
                label.IsLabelVisible && label.IsRectOnScreen
                && label.DisplayName.Equals(openGuardian, StringComparison.OrdinalIgnoreCase));
            var alreadyUsed = 6 - Math.Clamp(remainingPortals, 0, 6);
            if (saved is not null
                && saved.ObjectiveName.Equals(openGuardian, StringComparison.OrdinalIgnoreCase))
                RestoreRecoveryState(saved);
            while (_progress!.PortalEntriesThisEncounter < alreadyUsed)
                _progress.RecordPortalEntry();
            _objective = new GuardianRotaObjective(
                GuardianRotaObjectiveKind.GuardianMap, openGuardian,
                _progress.RotationsCompleted + 1);
            _activeStrategy = BuildStrategy(openGuardian, requireBoss: true);
            _resumeStep = Step.GuardianMap;
            _recoveryAllowed = true;
            _recoveryTransitionRequested = false;
            _recoverySawHub = false;
            _recoveryPortal.Reset();
            Advance(Step.Recover, $"re-entering existing {openGuardian} portal after restart");
            return;
        }
        if (saved is { Kind: GuardianRotaObjectiveKind.FormedInvitation, EncounterComplete: false })
        {
            RestoreRecoveryState(saved);
            _resumeStep = Step.FormedArena;
            _recoveryAllowed = true;
            _recoverySawHub = true;
            _recoveryTransitionRequested = false;
            _recoveryPortal.Reset();
            Advance(Step.Recover, "re-entering existing The Formed portal after restart");
            return;
        }
        if (saved is not null
            && (BotMonotonicClock.Now - _bootHydrationStartedAt).TotalSeconds < 3)
        {
            LastDecision = $"waiting for existing {saved.ObjectiveName} portal to hydrate";
            return;
        }
        // The encounter portal may legitimately be gone after a client restart. Abandon only the
        // unfinished objective; retain totals from earlier completed rotations.
        if (saved is not null)
        {
            _progress!.RestoreTotals(
                saved.RotationsCompleted,
                saved.InvitationsCompleted,
                saved.GuardianMapsCompleted);
            _progress.ResetEncounterBudget();
        }
        _roller.StartMaps(_settings.Current.GuardianMaxChaosRerolls);
        Advance(Step.PrepareMaps, "identifying and safely rolling carried Guardian maps");
    }

    private void TickPrepareMaps(BehaviorContext ctx)
    {
        var result = _roller.Tick(ctx);
        LastDecision = _roller.Status;
        if (result == GuardianItemRollController.Result.Failed) Stop(_roller.Status);
        else if (result == GuardianItemRollController.Result.Succeeded)
        {
            _scanner.Start();
            Advance(Step.ScanAtlas, "scanning current Maven witness state");
        }
    }

    private void TickScanAtlas(BehaviorContext ctx)
    {
        if (ctx.Snapshot.IsStashOpen)
        {
            ctx.Input.VerifiedTapKey(
                EscapeKeyVk, ClickIntent.InteractUi, "close stash after Guardian loot deposit",
                expectResolved: () => !(_getSnapshot()?.IsStashOpen ?? true),
                timeoutMs: 1500);
            LastDecision = "closing stash before Atlas witness scan";
            return;
        }
        if (ctx.Snapshot.Inventory.IsOpen)
        {
            ctx.Input.VerifiedTapKey(
                InventoryKeyVk, ClickIntent.InteractUi, "close inventory before Atlas scan",
                expectResolved: () => !(_getSnapshot()?.Inventory.IsOpen ?? true),
                timeoutMs: 1500);
            LastDecision = "closing inventory before Atlas witness scan";
            return;
        }
        var result = _scanner.Tick(ctx);
        LastDecision = _scanner.Status;
        if (result == GuardianAtlasScanner.Result.Failed) Stop(_scanner.Status);
        else if (result == GuardianAtlasScanner.Result.Succeeded)
            Advance(Step.Decide, "Guardian Atlas state verified");
    }

    private void TickDecide(BehaviorContext ctx)
    {
        if (ctx.Snapshot.AtlasPanel.IsVisible)
        {
            ctx.Input.VerifiedTapKey(
                AtlasKeyVk, ClickIntent.InteractUi, "close Atlas after Guardian scan",
                expectResolved: () => !(_getSnapshot()?.AtlasPanel.IsVisible ?? true),
                timeoutMs: 1500);
            LastDecision = "closing Atlas before next objective";
            return;
        }

        try { _objective = _progress!.Decide(_scanner.States); }
        catch (InvalidOperationException ex) { Stop(ex.Message); return; }
        if (_objective.Kind == GuardianRotaObjectiveKind.Finished)
        {
            _recoveryStore.Delete();
            _settings.Mutate(s => s.BotActive = false);
            Stop($"completed {_progress!.RotationsCompleted} full Guardian rotations");
            return;
        }
        if (_objective.Kind == GuardianRotaObjectiveKind.GuardianMap)
        {
            _encounterCompleteCertified = false;
            _activeStrategy = BuildStrategy(_objective.Name, requireBoss: true);
            _guardianCombat.SetTraversalOrigin(null);
            _lastPersistedTraversalOrigin = null;
            PersistRecovery();
            _device.Start(_getEntities(), MapDeviceSystem.PayloadSource.InventoryGuardianMap);
            Advance(Step.GuardianDevice, $"starting {_objective.Name}");
        }
        else
        {
            _encounterCompleteCertified = false;
            _activeStrategy = BuildStrategy("The Formed", requireBoss: false);
            PersistRecovery();
            Advance(Step.OpenKirac, "opening Kirac's The Formed invitation");
        }
    }

    private void TickGuardianDevice(BehaviorContext ctx)
    {
        var result = _device.Tick(ctx);
        LastDecision = _device.Status;
        if (result == MapDeviceSystem.Result.Failed) Stop(_device.Status);
        else if (result == MapDeviceSystem.Result.Succeeded)
        {
            if (WorldAreaClassifier.Classify(ctx) == AreaRole.SafeHub)
            {
                _resumeStep = Step.GuardianMap;
                _recoveryAllowed = true;
                _recoveryTransitionRequested = false;
                _recoverySawHub = false;
                _recoveryPortal.Reset();
                Advance(Step.Recover, "device portal click confirmed before encounter load; verifying transition");
                return;
            }
            if (!_progress!.RecordPortalEntry()) { Stop("Guardian portal budget exhausted"); return; }
            PersistRecovery();
            _guardianCombat.Reset();
            Advance(Step.GuardianMap, $"running {_objective.Name}");
        }
    }

    private void TickGuardianMap(GameSnapshot snapshot, IInputRouter input, BehaviorContext ctx)
    {
        if (WorldAreaClassifier.Classify(ctx) == AreaRole.SafeHub)
        {
            _resumeStep = Step.GuardianMap;
            _recoveryAllowed = true;
            _recoveryTransitionRequested = false;
            _recoverySawHub = false;
            _recoveryPortal.Reset();
            Advance(Step.Recover, "Guardian combat was still in the hub; re-entering encounter");
            return;
        }
        _guardianCombat.Tick(snapshot, input);
        if (_guardianCombat.TraversalOrigin is { } origin
            && (_lastPersistedTraversalOrigin is not { } savedOrigin
                || savedOrigin.X != origin.X || savedOrigin.Y != origin.Y))
        {
            _lastPersistedTraversalOrigin = origin;
            PersistRecovery();
        }
        LastDecision = _guardianCombat.LastDecision;
        if (_guardianCombat.RequestedAbandonReason is { } reason) { Stop(reason); return; }
        if (!_guardianCombat.IsCleared) return;
        _progress!.RecordGuardianClear();
        _encounterCompleteCertified = true;
        PersistRecovery();
        StartLeave(ctx, $"{_objective.Name} boss cleared");
    }

    private void TickOpenKirac(BehaviorContext ctx)
    {
        var receptacle = ctx.Snapshot.MapReceptacle;
        if (receptacle.IsVisible && receptacle.Item() is not null)
        {
            _roller.StartInvitation(
                _settings.Current.GuardianInvitationMinQuantity,
                _settings.Current.GuardianInvitationPreferredQuantity,
                _settings.Current.GuardianMaxChaosRerolls);
            Advance(Step.RollInvitation, "rolling The Formed invitation");
            return;
        }

        var dialog = NpcDialogView.Read(ctx.Snapshot.Reader, ctx.Snapshot.IngameStateAddress);
        var option = dialog.FindExact(FormedOption)
            .Where(x => x.Rect is { Width: > 0, Height: > 0 }).ToArray();
        if (option.Length == 1 && option[0].Rect is { } rect)
        {
            var point = ctx.Snapshot.Window.ToScreen(rect.CenterX, rect.CenterY);
            ctx.Input.Click(
                point.X, point.Y, ClickIntent.InteractUi, "select Invitation: The Formed",
                expectResolved: () => _getSnapshot()?.MapReceptacle.IsVisible ?? false,
                timeoutMs: 3000);
            LastDecision = "selecting exact Kirac The Formed option";
            return;
        }
        if (option.Length > 1) { Stop("Kirac exposed multiple exact The Formed controls"); return; }
        _openKirac.Tick(ctx);
        LastDecision = _openKirac.LastDecision;
    }

    private void TickRollInvitation(BehaviorContext ctx)
    {
        var result = _roller.Tick(ctx);
        LastDecision = _roller.Status;
        if (result == GuardianItemRollController.Result.Failed) Stop(_roller.Status);
        else if (result == GuardianItemRollController.Result.Succeeded)
        {
            _preInvitationPortals.Clear();
            foreach (var portal in ctx.Entities?.Entries.Values.Where(IsPortal) ?? [])
                _preInvitationPortals.Add(portal.Id);
            _formedActivateClicked = false;
            _formedPortal.Reset();
            Advance(Step.ActivateInvitation,
                $"activating safe The Formed at {_roller.FinalQuantity}% quantity");
        }
    }

    private void TickActivateInvitation(BehaviorContext ctx)
    {
        if (!_formedActivateClicked)
        {
            var receptacle = ctx.Snapshot.MapReceptacle;
            if (!receptacle.IsVisible || receptacle.Item() is null)
            {
                Stop("The Formed receptacle closed before activation");
                return;
            }
            if (receptacle.ActivateButtonRect() is not { } rect)
            {
                LastDecision = "waiting for The Formed Activate button";
                return;
            }
            var point = ctx.Snapshot.Window.ToScreen(rect.CenterX, rect.CenterY);
            var ticket = ctx.Input.Click(
                point.X, point.Y, ClickIntent.InteractUi, "activate The Formed",
                expectResolved: () => !(_getSnapshot()?.MapReceptacle.IsVisible ?? true),
                timeoutMs: 3000);
            if (ticket.Accepted) _formedActivateClicked = true;
            return;
        }

        var result = _formedPortal.Tick(ctx);
        LastDecision = "entering fresh The Formed portal";
        if (result == BehaviorStatus.Failure
            && (BotMonotonicClock.Now - _stepStartedAt).TotalSeconds > 30)
        {
            Stop("fresh The Formed portal was not found");
            return;
        }
        if (result == BehaviorStatus.Success)
        {
            if (!_progress!.RecordPortalEntry()) { Stop("The Formed portal budget exhausted"); return; }
            _formedCombat.Reset();
            _formedCompleteAt = TimeSpan.MinValue;
            Advance(Step.FormedArena, "entered The Formed; approaching podium");
        }
    }

    private void TickFormedArena(GameSnapshot snapshot, IInputRouter input, BehaviorContext ctx)
    {
        if (WorldAreaClassifier.Classify(ctx) == AreaRole.SafeHub)
        {
            _resumeStep = Step.FormedArena;
            _recoveryAllowed = true;
            _recoveryTransitionRequested = false;
            _recoverySawHub = true;
            _recoveryPortal.Reset();
            Advance(Step.Recover, "The Formed combat landed in the hub; re-entering encounter");
            return;
        }
        var podium = FindByPath(ctx, PodiumPath);
        if (podium is null)
        {
            LastDecision = "waiting for The Formed podium to stream";
            return;
        }
        var phase = ReadPodiumPhase(snapshot, podium);
        if (phase == MavenInvitationStates.BossRushObject.ReadyToStart)
        {
            _startPodium.Tick(ctx);
            LastDecision = _startPodium.LastDecision;
            return;
        }

        _formedCombat.Tick(snapshot, input);
        LastDecision = phase is null
            ? _formedCombat.LastDecision
            : $"The Formed {MavenInvitationStates.BossRushObject.Describe(phase.Value)}: {_formedCombat.LastDecision}";
        if (phase != MavenInvitationStates.BossRushObject.EncounterComplete) return;
        if (_formedCompleteAt == TimeSpan.MinValue) _formedCompleteAt = BotMonotonicClock.Now;
        // Keep the normal map loot controller alive while delayed splinters settle.
        if ((BotMonotonicClock.Now - _formedCompleteAt).TotalSeconds < 6) return;
        _progress!.RecordInvitationClear();
        _encounterCompleteCertified = true;
        PersistRecovery();
        StartLeave(ctx, "The Formed complete; reward sweep settled");
    }

    private void StartLeave(BehaviorContext ctx, string reason)
    {
        var returnOrigin = _objective.Kind == GuardianRotaObjectiveKind.FormedInvitation
            ? _formedCombat.TraversalOrigin
            : _guardianCombat.TraversalOrigin;
        _leave.Start(ctx, returnOrigin);
        Advance(Step.LeaveEncounter, reason);
    }

    private void TickLeave(BehaviorContext ctx)
    {
        var result = _leave.Tick(ctx);
        LastDecision = _leave.Status;
        if (result == LeaveMapSystem.Result.Failed) Stop(_leave.Status);
        else if (result == LeaveMapSystem.Result.Succeeded)
        {
            _guardianCombat.Reset();
            _formedCombat.Reset();
            if (_objective.Kind == GuardianRotaObjectiveKind.FormedInvitation)
            {
                _deposit.Start(_settings.Current.GuardianDumpTabName);
                Advance(Step.DepositLoot, "returned to hideout; depositing rotation loot");
            }
            else
            {
                _scanner.Start();
                Advance(Step.ScanAtlas, "returned to hideout; refreshing witness state");
            }
        }
    }

    private void TickDeposit(BehaviorContext ctx)
    {
        var result = _deposit.Tick(ctx);
        LastDecision = _deposit.Status;
        if (result == StashDepositSystem.Result.Failed) Stop(_deposit.Status);
        else if (result == StashDepositSystem.Result.Succeeded)
        {
            _scanner.Start();
            Advance(Step.ScanAtlas, "rotation loot deposited; refreshing witness state");
        }
    }

    public bool NotifyRevived()
    {
        if (_progress is null || _step is not (Step.GuardianMap or Step.FormedArena)) return false;
        _resumeStep = _step;
        _recoveryAreaHash = _getSnapshot()?.AreaHash ?? 0;
        _recoveryAllowed = _progress.RecordDeath();
        _recoveryTransitionRequested = false;
        _recoverySawHub = false;
        PersistRecovery();
        _recoveryPortal.Reset();
        Advance(Step.Recover, _recoveryAllowed
            ? "death recorded; waiting to re-enter existing portal"
            : "six-portal encounter budget exhausted");
        return true;
    }

    private void TickRecover(BehaviorContext ctx)
    {
        // Immediately after the resurrect panel closes PoE can expose one final arena
        // snapshot before entering its loading transition to the hideout. Do not mistake
        // that transient frame for a same-instance checkpoint revive.
        if ((BotMonotonicClock.Now - _stepStartedAt).TotalSeconds < 2)
        {
            LastDecision = "waiting for checkpoint revive destination to settle";
            return;
        }
        var role = WorldAreaClassifier.Classify(ctx);
        if (role != AreaRole.SafeHub)
        {
            if (_recoveryAllowed)
            {
                if ((_recoveryTransitionRequested || _recoverySawHub)
                    && !_progress!.RecordPortalEntry())
                {
                    Stop("encounter portal budget exhausted");
                    return;
                }
                if (_resumeStep == Step.FormedArena)
                    _formedCombat.PrepareForSameInstanceCheckpointRecovery();
                else
                    _guardianCombat.PrepareForSameInstanceCheckpointRecovery();
                PersistRecovery();
                Advance(_resumeStep, _recoveryTransitionRequested
                    ? "encounter transition verified outside hub; traversal reset"
                    : "revived at same-instance checkpoint; traversal reset");
                return;
            }
            LastDecision = "waiting for checkpoint revive destination";
            return;
        }
        if (!_recoveryAllowed) { Stop("encounter failed after exhausting six portals"); return; }
        _recoverySawHub = true;
        var result = _recoveryPortal.Tick(ctx);
        LastDecision = "re-entering encounter after death";
        if (result == BehaviorStatus.Failure
            && (BotMonotonicClock.Now - _stepStartedAt).TotalSeconds > 30)
        {
            Stop("existing encounter portal could not be re-entered");
            return;
        }
        if (result == BehaviorStatus.Success)
        {
            _recoveryTransitionRequested = true;
            LastDecision = "portal click confirmed; waiting until the character is outside the hub";
        }
    }

    private void PersistRecovery()
    {
        if (_progress is null || string.IsNullOrWhiteSpace(_objective.Name)
            || _objective.Kind == GuardianRotaObjectiveKind.Finished)
            return;
        var traversalOrigin = _objective.Kind == GuardianRotaObjectiveKind.FormedInvitation
            ? _formedCombat.TraversalOrigin
            : _guardianCombat.TraversalOrigin;
        _recoveryStore.Save(new GuardianRotaRecoveryState(
            _objective.Kind,
            _objective.Name,
            _progress.PortalEntriesThisEncounter,
            _progress.DeathsThisEncounter,
            DateTime.UtcNow,
            traversalOrigin?.X,
            traversalOrigin?.Y,
            _encounterCompleteCertified,
            _progress.RotationsCompleted,
            _progress.InvitationsCompleted,
            _progress.GuardianMapsCompleted,
            ProgressVersion: 1));
    }

    private void RestoreRecoveryState(GuardianRotaRecoveryState saved)
    {
        _progress ??= new GuardianRotaProgress(_settings.Current.GuardianTargetRotations);
        _progress.RestoreTotals(
            saved.RotationsCompleted,
            saved.InvitationsCompleted,
            saved.GuardianMapsCompleted);
        _progress.ResetEncounterBudget();
        for (var i = 0; i < Math.Clamp(saved.PortalEntries, 0, 6); i++)
            _progress.RecordPortalEntry();
        for (var i = 0; i < Math.Clamp(saved.Deaths, 0, 5); i++)
            _progress.RecordDeath();
        _objective = new GuardianRotaObjective(saved.Kind, saved.ObjectiveName, 1);
        _encounterCompleteCertified = saved.EncounterComplete;
        _activeStrategy = BuildStrategy(saved.ObjectiveName,
            requireBoss: saved.Kind == GuardianRotaObjectiveKind.GuardianMap);
        if (saved.TraversalOriginX is { } originX && saved.TraversalOriginY is { } originY)
        {
            var origin = new Vector2i { X = originX, Y = originY };
            if (saved.Kind == GuardianRotaObjectiveKind.FormedInvitation)
                _formedCombat.SetTraversalOrigin(origin);
            else
                _guardianCombat.SetTraversalOrigin(origin);
            _lastPersistedTraversalOrigin = origin;
        }
        _resumeStep = saved.Kind == GuardianRotaObjectiveKind.FormedInvitation
            ? Step.FormedArena : Step.GuardianMap;
        _recoveryAllowed = true;
        _recoveryTransitionRequested = false;
        _recoverySawHub = false;
    }

    public void Reset()
    {
        _combat.Movement.Release();
        _combat.ResetCombat();
        _device.Cancel();
        _leave.Cancel();
        _deposit.Cancel();
        _scanner.Reset();
        _roller.Reset();
        _openKirac.Reset();
        _startPodium.Reset();
        _formedPortal.Reset();
        _recoveryPortal.Reset();
    }

    public void RestartStoppedRun()
    {
        if (!_stopped) return;
        Reset();
        _stopped = false;
        _stopReason = string.Empty;
        _progress = null;
        _objective = default;
        _activeStrategy = null;
        _step = Step.Boot;
        _stepStartedAt = BotMonotonicClock.Now;
        LastDecision = "ready for a new Guardian Rota session";
    }

    private void Advance(Step step, string decision)
    {
        _step = step;
        _stepStartedAt = BotMonotonicClock.Now;
        LastDecision = decision;
    }

    private void Stop(string reason)
    {
        _stopped = true;
        _stopReason = reason;
        _step = Step.Stopped;
        LastDecision = reason;
        _settings.Mutate(s => s.BotActive = false);
        _combat.Movement.Release();
        Diagnostics.EventLog.Emit(
            "guardian-rota", "guardian-rota.stopped", Diagnostics.EventSeverity.Error, reason);
    }

    private static FarmingStrategy BuildStrategy(string mapName, bool requireBoss)
        => new()
        {
            Identity = new StrategyIdentity { Name = $"Guardian Rota - {mapName}" },
            Supply = new SupplySection
            {
                Map = new MapSupply
                {
                    Source = MapSource.PlayerInventory,
                    TargetMapName = mapName,
                },
            },
            MapPrep = new MapPrepSection { AtlasNodeName = mapName },
            Loot = new LootStrategySection { DepositAfterEachMap = false },
            Completion = new CompletionSection
            {
                RequireBossKill = requireBoss,
                ExplorationDonePercent = 100,
                TargetMaps = 1,
            },
            Limits = new LimitsSection { MaxMapMinutes = 0 },
        };

    private static EntityCache.Entry? FindByPath(BehaviorContext ctx, string path)
        => ctx.Entities?.Entries.Values.FirstOrDefault(x =>
            !x.IsStale && x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

    private static bool IsPortal(EntityCache.Entry entity)
        => !entity.IsStale
        && entity.Kind is EntityListReader.EntityKind.Portal or EntityListReader.EntityKind.TownPortal;

    private static bool IsGuardianSupply(InventoryView.Item item)
        => InventoryView.IsRetainedSupply(item)
        || item.Path.Equals(GuardianItemRollController.GuardianMapPath, StringComparison.OrdinalIgnoreCase)
        || item.Path is "Metadata/Items/Currency/CurrencyIdentification"
            or "Metadata/Items/Currency/CurrencyConvertToNormal"
            or "Metadata/Items/Currency/CurrencyUpgradeToRare"
            or "Metadata/Items/Currency/CurrencyRerollRare";

    private static long? ReadPodiumPhase(GameSnapshot snapshot, EntityCache.Entry podium)
        => podium.StateMachineCompAddr == 0 ? null : StateMachineView.ReadValue(
            snapshot.Reader, podium.StateMachineCompAddr,
            MavenInvitationStates.BossRushObject.CurrentPhase);
}
