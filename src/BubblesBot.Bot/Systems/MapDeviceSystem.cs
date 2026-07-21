using BubblesBot.Bot.Behaviors;
using BubblesBot.Bot.Behaviors.Movement;
using BubblesBot.Bot.Input;
using BubblesBot.Bot.Settings;
using BubblesBot.Bot.Strategies;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Systems;

/// <summary>
/// State machine that drives the hideout-to-map handoff: walk to the map device, click it,
/// stage a map from atlas storage, click Activate, wait for portals to spawn, walk into a
/// portal. v1 is intentionally minimal — no scarab insertion, no Atlas-tree node selection,
/// no inventory fragment fallback. Mirrors AutoExile's <c>MapDeviceSystem</c> at the phase
/// level but trimmed to the path the user described: "right-click a map already in storage,
/// confirm activate enables, click activate, wait for portals, enter."
///
/// <para><b>Map selection.</b> v1 clicks a single configured storage slot index
/// (<see cref="BotSettings.BlightStorageSlotIndex"/>) — the user has pre-loaded only Blight
/// maps in storage so a positional click is equivalent to a typed filter. A future revision
/// can read the item's <c>Mods</c> component for an "InfectedMap" mod and pick by content;
/// for now the slot index is sufficient and avoids needing the mods reader to be wired up.</para>
///
/// <para><b>Click flow.</b> Right-click on the storage slot — PoE's behavior: with the atlas
/// + device open, right-click on a stored map auto-stages it into the device's first empty
/// slot. We then verify by polling <see cref="AtlasPanelView.DeviceSlot(int)"/> until slot 0
/// reports occupied. Click Activate (verified by the activate label going invisible /
/// portals appearing). Wait for at least one <c>BlightPortal</c>-style portal entity. Walk
/// to it, click, transition.</para>
/// </summary>
public sealed class MapDeviceSystem
{
    public enum PayloadSource { AtlasStorage, InventorySimulacrum, InventoryMap, InventoryNormalMap }
    public enum Phase
    {
        Idle,
        NavigateToDevice,   // walk player into interaction range of the map device entity
        OpenDevice,         // click the device → atlas panel becomes visible
        SelectMap,          // right-click the configured storage slot → slot 0 occupied
        Activate,           // click activate → portals start spawning
        WaitForPortals,     // entity-list scan for the spawned portal
        EnterPortal,        // walk into the portal → area transition
        Done,
        Failed,
    }

    public enum Result { InProgress, Succeeded, Failed }

    public Phase CurrentPhase { get; private set; } = Phase.Idle;
    public string Status { get; private set; } = "idle";
    public bool IsBusy => CurrentPhase != Phase.Idle && CurrentPhase != Phase.Done && CurrentPhase != Phase.Failed;

    private readonly Func<GameSnapshot?> _getSnapshot;
    private readonly Func<LivePlayer?>   _getLive;
    private readonly Func<EntityCache?>  _getEntities;
    private readonly MovementSystem      _movement;
    private readonly SkillBook           _skills;
    private readonly FollowPath          _approach;

    private TimeSpan _phaseStartedAt;
    private TimeSpan _lastActionAt;
    private TimeSpan _activateReadySince = TimeSpan.MinValue;
    private TimeSpan _portalInRangeSince = TimeSpan.MinValue;
    private uint     _deviceEntityId;
    private uint     _portalEntityId;
    private int      _clickAttempts;
    private int      _nodeClickAttempts;
    private int      _nodeHoverUiIndex = -1;
    private TimeSpan _nodeHoverStartedAt = TimeSpan.MinValue;
    private int      _nodePanAttempts;
    private int      _nodePanAnchorCandidate;
    private TimeSpan _nodePanHoverStartedAt = TimeSpan.MinValue;
    private PayloadSource _payloadSource;
    private Vector2i? _deviceAccessGoal;
    private bool _oldPortalAccessPrepared;
    private int _deviceAccessAttempt;

    /// <summary>
    /// Portal entity IDs that existed when <see cref="Start"/> was called. The hideout
    /// often retains the previous map's portals; without this snapshot the bot detects
    /// them in <see cref="TickActivate"/>, decides "portal already spawned, must have
    /// activated," and walks into a stale portal back to the previous map. We only count
    /// portals whose IDs are NOT in this set — the activation truly spawned new ones.
    /// </summary>
    private readonly HashSet<uint> _preFlowPortalIds = new();
    private const int  ActionCooldownMs       = 500;
    private const int  MaxClickAttempts       = 5;
    private const int  PhaseTimeoutSeconds    = 30;
    private const int  PortalWaitTimeoutSec   = 15;
    private const int  PortalEntryTimeoutSec  = 75;
    /// <summary>Settle window after a Ctrl+click insert before re-checking the device slot —
    /// the device UI takes a beat to render the staged item + Activate button. Mirrors
    /// AutoExile's <c>InsertSettleMs</c>; without it we double-insert.</summary>
    private const int  InsertSettleMs         = 800;
    private const int  ActivateReadySettleMs  = 700;
    private const int  PortalInRangeSettleMs  = 350;
    private const int  PortalClickTimeoutMs   = 3000;

    /// <summary>VK_LCONTROL — modifier for "Ctrl+click to insert" (alternate path to right-click).</summary>
    private const int VK_LCONTROL = 0xA2;
    private const int VK_INVENTORY = 0x49;
    // Validated post-patch in Standard on 2026-07-20: Files.AtlasNodes index 4 (Mesa) is
    // canvas child 6, so the current prefix is +2. This changed from +4 without the AtlasNodes
    // file ordering changing, which is why every click is now guarded by an exact UIHover
    // ancestry check before dispatch.
    internal const int CurrentAtlasNodeUiPrefix = 2;
    private const int AtlasNodeHoverSettleMs = 120;
    private const int MaxAtlasPanAttempts = 8;
    private static readonly (double X, double Y)[] AtlasPanAnchors =
    [
        (0.44, 0.54),
        (0.36, 0.62),
        (0.52, 0.70),
        (0.31, 0.44),
    ];

    public MapDeviceSystem(MovementSystem movement, SkillBook skills,
        Func<GameSnapshot?> getSnapshot, Func<LivePlayer?> getLive, Func<EntityCache?> getEntities)
    {
        _movement    = movement;
        _skills      = skills;
        _getSnapshot = getSnapshot;
        _getLive     = getLive;
        _getEntities = getEntities;
        // The portal-clear access point is a navigation waypoint, not an interactable.
        // FollowPath's default arrival radius is InteractionRangeGrid, which can report
        // success 20+ cells before this point while TickNavigateToDevice deliberately waits
        // for a tight 6-cell arrival. Keep both sides of that contract aligned so an old
        // portal ring cannot leave the flow parked forever just short of the safe side.
        _approach    = new FollowPath("mapdev/approach", movement, GetDeviceGoal, skills,
            goalArrivalRadius: 6f, allowGapCrossing: false);
    }

    public void Start(
        EntityCache? entities = null,
        PayloadSource payloadSource = PayloadSource.AtlasStorage)
    {
        CurrentPhase    = Phase.NavigateToDevice;
        _phaseStartedAt = BotMonotonicClock.Now;
        _lastActionAt   = TimeSpan.Zero;
        _deviceEntityId = 0;
        _portalEntityId = 0;
        _clickAttempts  = 0;
        _nodeClickAttempts = 0;
        _nodeHoverUiIndex = -1;
        _nodeHoverStartedAt = TimeSpan.MinValue;
        _nodePanAttempts = 0;
        _nodePanAnchorCandidate = 0;
        _nodePanHoverStartedAt = TimeSpan.MinValue;
        _activateReadySince = TimeSpan.MinValue;
        _portalInRangeSince = TimeSpan.MinValue;
        _payloadSource = payloadSource;
        _deviceAccessGoal = null;
        _oldPortalAccessPrepared = false;
        _deviceAccessAttempt = 0;

        // Snapshot existing portals so post-activate detection only fires on NEW ones.
        _preFlowPortalIds.Clear();
        if (entities is not null)
        {
            foreach (var e in entities.Entries.Values)
            {
                if (string.IsNullOrEmpty(e.Path)) continue;
                if (!e.Path.Contains("Portal", StringComparison.OrdinalIgnoreCase)) continue;
                if (e.Path.Contains("/BlightPortal", StringComparison.Ordinal)) continue;
                _preFlowPortalIds.Add(e.Id);
            }
        }

        Status = "navigating to map device";
        BubblesBot.Bot.Diagnostics.EventLog.Log("MapDevice",
            $"started payload={_payloadSource} (pre-flow portals: {_preFlowPortalIds.Count})");
    }

    public void Cancel()
    {
        CurrentPhase = Phase.Idle;
        _movement.Release();
        _approach.Reset();
        Status = "cancelled";
    }

    public Result Tick(BehaviorContext ctx)
    {
        if (!IsBusy)
            return CurrentPhase == Phase.Done ? Result.Succeeded
                 : CurrentPhase == Phase.Failed ? Result.Failed
                 : Result.InProgress;

        // Phase timeout (portal wait gets its own).
        var phaseAge = (BotMonotonicClock.Now - _phaseStartedAt).TotalSeconds;
        var timeout  = CurrentPhase switch
        {
            Phase.WaitForPortals => PortalWaitTimeoutSec,
            // A portal click can legitimately spend eight seconds waiting for a loading
            // transition. Give all six portals a chance instead of abandoning an already
            // activated map after only three attempts.
            Phase.EnterPortal => PortalEntryTimeoutSec,
            _ => PhaseTimeoutSeconds,
        };
        if (phaseAge > timeout)
        {
            return Fail($"timeout in {CurrentPhase} after {phaseAge:F0}s — last status: {Status}");
        }

        // Action cooldown — keeps us from spamming clicks faster than PoE can register UI changes.
        if ((BotMonotonicClock.Now - _lastActionAt).TotalMilliseconds < ActionCooldownMs)
            return Result.InProgress;

        return CurrentPhase switch
        {
            Phase.NavigateToDevice => TickNavigateToDevice(ctx),
            Phase.OpenDevice       => TickOpenDevice(ctx),
            Phase.SelectMap        => TickSelectMap(ctx),
            Phase.Activate         => TickActivate(ctx),
            Phase.WaitForPortals   => TickWaitForPortals(ctx),
            Phase.EnterPortal      => TickEnterPortal(ctx),
            _ => Result.InProgress,
        };
    }

    // ─── Phases ──────────────────────────────────────────────────────────

    private Result TickNavigateToDevice(BehaviorContext ctx)
    {
        // The device panel itself is stronger completion evidence than any geometric
        // access-point heuristic. This commonly occurs after a retry/re-arm while old map
        // portals still surround the device: do not spend 30 seconds trying to reach a
        // six-grid waypoint for a device that is already open and interactive.
        if (ctx.Snapshot.AtlasPanel.IsVisible)
        {
            _movement.Release();
            _approach.Reset();
            _deviceAccessGoal = null;
            _oldPortalAccessPrepared = true;
            _clickAttempts = 0;
            return Advance(Phase.SelectMap, "device already open — selecting map");
        }

        var device = FindMapDevice(ctx);
        if (device is null)
        {
            Status = "no map device found in area";
            return Result.InProgress;
        }
        _deviceEntityId = device.Id;

        if (ctx.Live is null) { Status = "no live player"; return Result.InProgress; }
        var player = ctx.Live.Value.GridPosition;
        var dist = Distance(player, device.GridPosition);

        if (_deviceAccessGoal is { } accessGoal)
        {
            if (Distance(player, accessGoal) > 6f)
            {
                var approach = _approach.Tick(ctx);
                if (approach == BehaviorStatus.Failure)
                {
                    _deviceAccessAttempt++;
                    if (_deviceAccessAttempt >= 16)
                        return Fail("no reachable portal-clear approach to map device after 16 candidates");

                    var oldPortals = ctx.Entities?.Entries.Values
                        .Where(entity => !entity.IsStale && _preFlowPortalIds.Contains(entity.Id))
                        .Select(entity => entity.GridPosition)
                        .ToArray() ?? [];
                    _deviceAccessGoal = MapDeviceAccessPolicy.Choose(
                        device.GridPosition, player, oldPortals,
                        ctx.Settings.InteractionRangeGrid, _deviceAccessAttempt);
                    _approach.Reset();
                    Status = _deviceAccessGoal is { } rotated
                        ? $"portal-clear route {_deviceAccessAttempt + 1}/16 via ({rotated.X},{rotated.Y})"
                        : "old portals disappeared - approaching device directly";
                    if (_deviceAccessGoal is null) _oldPortalAccessPrepared = true;
                    return Result.InProgress;
                }
                Status = $"taking portal-clear device approach via ({accessGoal.X},{accessGoal.Y})";
                return Result.InProgress;
            }
            _movement.Release();
            _approach.Reset();
            _deviceAccessGoal = null;
            _oldPortalAccessPrepared = true;
            Status = "portal-clear device approach reached";
            return Result.InProgress;
        }

        // Old map portals form dynamic collision around the device and can also intercept a
        // projected world click. Approach one safe side of the device before opening it.
        if (!_oldPortalAccessPrepared && _preFlowPortalIds.Count > 0 && ctx.Entities is { } entities)
        {
            var oldPortals = entities.Entries.Values
                .Where(entity => !entity.IsStale && _preFlowPortalIds.Contains(entity.Id))
                .Select(entity => entity.GridPosition)
                .ToArray();
            _deviceAccessGoal = MapDeviceAccessPolicy.Choose(
                device.GridPosition, player, oldPortals, ctx.Settings.InteractionRangeGrid,
                _deviceAccessAttempt);
            if (_deviceAccessGoal is not null)
            {
                _approach.Reset();
                Status = $"routing around {_preFlowPortalIds.Count} old portal(s) before device click";
                return Result.InProgress;
            }
            _oldPortalAccessPrepared = true;
        }

        if (dist <= ctx.Settings.InteractionRangeGrid)
        {
            _movement.Release();
            return Advance(Phase.OpenDevice, "in range — opening device");
        }

        // FollowPath drives walking via _approach (constructed with GetDeviceGoal).
        _approach.Tick(ctx);
        Status = $"approaching map device (dist={dist:F0})";
        return Result.InProgress;
    }

    private Result TickOpenDevice(BehaviorContext ctx)
    {
        var snap = ctx.Snapshot;
        var atlas = snap.AtlasPanel;
        if (atlas.IsVisible)
        {
            _clickAttempts = 0;
            return Advance(Phase.SelectMap, "device open — selecting map");
        }

        if (_clickAttempts >= MaxClickAttempts)
            return Fail($"failed to open device after {MaxClickAttempts} clicks");

        // Click the device entity. Resolve its bounds-projected screen point via the same
        // path InteractWorldEntity uses (re-implemented inline here to keep this system
        // self-contained — the click is a single one-shot, not a behavior tree).
        var device = FindMapDevice(ctx);
        if (device is null) return Fail("device entity disappeared mid-flow");
        if (ctx.Live is null) return Result.InProgress;

        var clickPoint = ResolveEntityClickPoint(ctx, device);
        if (clickPoint is null) { Status = "couldn't project device click point"; return Result.InProgress; }

        var ticket = ctx.Input.Click(clickPoint.Value.X, clickPoint.Value.Y,
            ClickIntent.InteractWorld, "map device open",
            expectResolved: () => _getSnapshot()?.AtlasPanel.IsVisible ?? false, timeoutMs: 2000);
        if (ticket.Accepted)
        {
            _clickAttempts++;
            _lastActionAt = BotMonotonicClock.Now;
            Status = $"clicked device ({_clickAttempts}/{MaxClickAttempts})";
            BubblesBot.Bot.Diagnostics.EventLog.Log("MapDevice", $"open click sent abs=({clickPoint.Value.X},{clickPoint.Value.Y})");
        }
        return Result.InProgress;
    }

    private Result TickSelectMap(BehaviorContext ctx)
    {
        if (_payloadSource == PayloadSource.InventorySimulacrum)
            return TickSelectSimulacrum(ctx);
        if (_payloadSource == PayloadSource.InventoryMap)
            return TickSelectCarriedMap(ctx);
        if (_payloadSource == PayloadSource.InventoryNormalMap)
            return TickSelectCarriedNormalMap(ctx);

        var atlas = ctx.Snapshot.AtlasPanel;
        if (!atlas.IsVisible) return Fail("atlas panel closed unexpectedly during map select");

        // Already-staged check: slot 0 occupied → move on.
        var slot0 = atlas.DeviceSlot(0);
        if (slot0 is { IsOccupied: true })
        {
            if (!TargetMapSelected(ctx, atlas, out var selectionError))
                return TickEjectWrongNodeMap(ctx, atlas, selectionError);
            if (!StackedDeckScarabLoadoutReady(ctx, atlas, out var loadoutError))
                return Fail(loadoutError);
            _clickAttempts = 0;
            return Advance(Phase.Activate, "map staged — clicking activate");
        }

        // The device sub-panel (atlas child [7]) only becomes visible once a map NODE is
        // selected on the atlas. A stored map can only be Ctrl+clicked in once it's up.
        // v1 does NOT auto-select the node — that requires PoE's AtlasNodes data file, which
        // BubblesBot is forbidden from parsing. The user selects their farm map node once;
        // PoE remembers it across device opens, so the panel comes up on every open.
        if (!atlas.IsDevicePanelVisible())
            return TickSelectAtlasNode(ctx);

        if (!TargetMapSelected(ctx, atlas, out var selectedError))
        {
            if (atlas.SelectedMapName().Length == 0
                && (BotMonotonicClock.Now - _lastActionAt).TotalSeconds < 2)
            {
                Status = "waiting for selected map name";
                return Result.InProgress;
            }
            // Atlas remembers the last node (often the previous farm's map). An empty device
            // on the wrong node is safe to correct by selecting our target. Never do this once
            // slot 0 is occupied: that path is rejected by the staged check above.
            Status = $"{selectedError}; selecting configured node";
            return TickSelectAtlasNode(ctx);
        }

        // Settle after an insert click so the device can render the staged item before we
        // re-evaluate / re-click.
        if ((BotMonotonicClock.Now - _lastActionAt).TotalMilliseconds < InsertSettleMs)
        {
            Status = "waiting for device to update after insert";
            return Result.InProgress;
        }

        if (_clickAttempts >= MaxClickAttempts)
            return Fail($"failed to stage map after {MaxClickAttempts} ctrl+clicks");

        var slotIndex = ctx.Settings.BlightStorageSlotIndex;
        var stored = atlas.StoredItems();
        StoredItemRef? target = null;
        foreach (var s in stored)
        {
            if (s.Index == slotIndex
                && s.Path.Contains(InventoryView.MapPathFragment, StringComparison.OrdinalIgnoreCase))
            {
                target = new StoredItemRef(s.Index, s.Rect);
                break;
            }
        }
        // If the configured index isn't present, fall back to the first stored item.
        if (target is null && stored.Count > 0)
        {
            var first = stored.FirstOrDefault(item => item.Path.Contains(
                InventoryView.MapPathFragment, StringComparison.OrdinalIgnoreCase));
            if (first is not null)
            {
                target = new StoredItemRef(first.Index, first.Rect);
                BubblesBot.Bot.Diagnostics.EventLog.Log("MapDevice",
                    $"slot {slotIndex} is not a MapKey — falling back to first map (index {first.Index} path={first.Path})");
            }
        }
        if (target is null) return Fail("no maps in storage");

        var (sx, sy) = ctx.Snapshot.Window.ToScreen((int)target.Value.Rect.CenterX, (int)target.Value.Rect.CenterY);
        // Farming flow: Ctrl+click transfers the stored map into the selected node's device
        // slot. (AutoExile uses Ctrl+click for named/farming maps; right-click is only for
        // boss/blight/sim fragments that auto-select their own node — not our case.)
        var ticket = ctx.Input.ModifierClick(sx, sy, new[] { VK_LCONTROL }, ClickIntent.InteractUi,
            $"insert storage[{target.Value.Index}]",
            expectResolved: () => _getSnapshot()?.AtlasPanel.DeviceSlot(0) is { IsOccupied: true },
            timeoutMs: 1500);
        if (ticket.Accepted)
        {
            _clickAttempts++;
            _lastActionAt = BotMonotonicClock.Now;
            Status = $"ctrl+clicked storage[{target.Value.Index}] ({_clickAttempts}/{MaxClickAttempts})";
            BubblesBot.Bot.Diagnostics.EventLog.Log("MapDevice", $"insert Ctrl+click sent at ({sx},{sy}) idx={target.Value.Index}");
        }
        return Result.InProgress;
    }

    private Result TickEjectWrongNodeMap(
        BehaviorContext ctx, AtlasPanelView atlas, string selectionError)
    {
        if ((BotMonotonicClock.Now - _lastActionAt).TotalMilliseconds < InsertSettleMs)
        {
            Status = $"{selectionError}; waiting to return staged map";
            return Result.InProgress;
        }
        if (_clickAttempts >= MaxClickAttempts)
            return Fail($"{selectionError}; failed to return staged map after {MaxClickAttempts} attempts");
        if (atlas.DeviceSlot(0) is not { Rect: { } rect, IsOccupied: true })
            return Result.InProgress;

        var (x, y) = ctx.Snapshot.Window.ToScreen((int)rect.CenterX, (int)rect.CenterY);
        var ticket = ctx.Input.ModifierClick(
            x, y, [VK_LCONTROL], ClickIntent.InteractUi,
            "return map staged under wrong atlas node",
            expectResolved: () => _getSnapshot()?.AtlasPanel.DeviceSlot(0) is { IsOccupied: false },
            timeoutMs: 2500);
        if (ticket.Accepted)
        {
            _clickAttempts++;
            _lastActionAt = BotMonotonicClock.Now;
            Status = $"{selectionError}; returning staged map ({_clickAttempts}/{MaxClickAttempts})";
            Diagnostics.EventLog.Emit(
                "MapDevice", "map-device.wrong-node-map-returned",
                Diagnostics.EventSeverity.Warning, Status);
        }
        return Result.InProgress;
    }

    private Result TickSelectAtlasNode(BehaviorContext ctx)
    {
        var nodeName = ctx.Strategy?.MapPrep.AtlasNodeName.Trim() ?? "";
        if (nodeName.Length == 0)
            return Fail("no atlas node configured for the active strategy");
        // Node → click-coordinate data is a per-patch built-in catalog (AtlasNodes data isn't
        // parsed). An unsupported node name fails closed rather than clicking blindly.
        if (!AtlasNodeCatalog.TryGetDataIndex(nodeName, out var dataIndex))
            return Fail($"atlas node '{nodeName}' is not in this build's node catalog (supported: {string.Join(", ", AtlasNodeCatalog.SupportedNames)})");
        if (_nodeClickAttempts >= MaxClickAttempts)
            return Fail($"failed to select '{nodeName}' after {MaxClickAttempts} clicks");
        if ((BotMonotonicClock.Now - _lastActionAt).TotalMilliseconds < InsertSettleMs)
        {
            Status = "waiting for atlas node selection to settle";
            return Result.InProgress;
        }

        var uiIndex = dataIndex + CurrentAtlasNodeUiPrefix;
        var rect = ctx.Snapshot.AtlasPanel.AtlasCanvasChildRect(uiIndex);
        if (rect is null) return Fail($"'{nodeName}' atlas node child {uiIndex} has no rectangle");
        var centerX = rect.Value.CenterX;
        var centerY = rect.Value.CenterY;
        if (!AtlasNodeInSafeViewport(centerX, centerY,
                ctx.Snapshot.Window.Width, ctx.Snapshot.Window.Height))
            return TickPanAtlasNode(ctx, nodeName, uiIndex, centerX, centerY);

        _nodePanAnchorCandidate = 0;
        _nodePanHoverStartedAt = TimeSpan.MinValue;

        var (x, y) = ctx.Snapshot.Window.ToScreen((int)centerX, (int)centerY);

        // Move first, then prove the topmost UI element at that pixel is this exact atlas
        // child. A node may have valid on-screen geometry while inventory/equipment is drawn
        // above it; clicking from geometry alone can mutate gear. Never dispatch that click.
        if (_nodeHoverUiIndex != uiIndex)
        {
            ctx.Input.HoverAt(x, y, CursorPriority.CombatAim);
            _nodeHoverUiIndex = uiIndex;
            _nodeHoverStartedAt = BotMonotonicClock.Now;
            Status = $"hovering {nodeName} atlas child {uiIndex} for identity check";
            return Result.InProgress;
        }
        if ((BotMonotonicClock.Now - _nodeHoverStartedAt).TotalMilliseconds < AtlasNodeHoverSettleMs)
        {
            ctx.Input.HoverAt(x, y, CursorPriority.CombatAim);
            Status = $"waiting for {nodeName} atlas hover identity";
            return Result.InProgress;
        }
        var hover = UiHoverView.Read(ctx.Snapshot.Reader, ctx.Snapshot.IngameStateAddress);
        if (!ctx.Snapshot.AtlasPanel.HoverBelongsToAtlasCanvasChild(hover.Element, uiIndex))
        {
            ctx.Input.HoverAt(x, y, CursorPriority.CombatAim);
            if ((BotMonotonicClock.Now - _nodeHoverStartedAt).TotalSeconds >= 2)
                return Fail($"'{nodeName}' atlas child {uiIndex} is obscured or not hoverable; refusing click");
            Status = $"proving {nodeName} atlas child {uiIndex} is unobscured";
            return Result.InProgress;
        }

        var ticket = ctx.Input.Click(
            x, y, ClickIntent.InteractUi, $"select {nodeName} atlas node",
            expectResolved: () => _getSnapshot()?.AtlasPanel is { } live
                && live.IsDevicePanelVisible()
                && live.SelectedMapName().Equals(nodeName, StringComparison.OrdinalIgnoreCase),
            timeoutMs: 2500);
        if (ticket.Accepted)
        {
            _nodeClickAttempts++;
            _nodeHoverUiIndex = -1;
            _nodeHoverStartedAt = TimeSpan.MinValue;
            _lastActionAt = BotMonotonicClock.Now;
            Status = $"clicked {nodeName} atlas child {uiIndex} ({_nodeClickAttempts}/{MaxClickAttempts})";
            BubblesBot.Bot.Diagnostics.EventLog.Emit(
                "MapDevice", "map-device.node-selection-requested",
                BubblesBot.Bot.Diagnostics.EventSeverity.Info, Status,
                new Dictionary<string, object?>
                {
                    ["targetMap"] = nodeName,
                    ["atlasDataIndex"] = dataIndex,
                    ["uiPrefix"] = CurrentAtlasNodeUiPrefix,
                    ["uiIndex"] = uiIndex,
                    ["screenX"] = x,
                    ["screenY"] = y,
                });
        }
        return Result.InProgress;
    }

    private Result TickPanAtlasNode(
        BehaviorContext ctx, string nodeName, int uiIndex, float centerX, float centerY)
    {
        if (_nodePanAttempts >= MaxAtlasPanAttempts)
            return Fail($"failed to pan '{nodeName}' atlas child {uiIndex} into an unobstructed viewport");

        var window = ctx.Snapshot.Window;
        if (_nodePanAnchorCandidate >= AtlasPanAnchors.Length)
            return Fail("no unobstructed atlas background point was available for panning");

        var anchorRatio = AtlasPanAnchors[_nodePanAnchorCandidate];
        var anchorX = (int)(window.Width * anchorRatio.X);
        var anchorY = (int)(window.Height * anchorRatio.Y);
        var (startX, startY) = window.ToScreen(anchorX, anchorY);

        if (_nodePanHoverStartedAt == TimeSpan.MinValue)
        {
            ctx.Input.HoverAt(startX, startY, CursorPriority.CombatAim);
            _nodePanHoverStartedAt = BotMonotonicClock.Now;
            Status = $"finding safe atlas background for {nodeName} pan";
            return Result.InProgress;
        }
        if ((BotMonotonicClock.Now - _nodePanHoverStartedAt).TotalMilliseconds < AtlasNodeHoverSettleMs)
        {
            ctx.Input.HoverAt(startX, startY, CursorPriority.CombatAim);
            return Result.InProgress;
        }

        var hover = UiHoverView.Read(ctx.Snapshot.Reader, ctx.Snapshot.IngameStateAddress);
        var directChild = ctx.Snapshot.AtlasPanel.AtlasCanvasDirectChildForHover(hover.Element);
        if (directChild < 0 || directChild >= CurrentAtlasNodeUiPrefix)
        {
            _nodePanAnchorCandidate++;
            _nodePanHoverStartedAt = TimeSpan.MinValue;
            Status = $"atlas pan anchor was covered (child {directChild}); trying another";
            return Result.InProgress;
        }

        var desiredX = window.Width * 0.42;
        var desiredY = window.Height * 0.46;
        var deltaX = Math.Clamp(desiredX - centerX, -window.Width * 0.18, window.Width * 0.18);
        var deltaY = Math.Clamp(desiredY - centerY, -window.Height * 0.22, window.Height * 0.22);
        var endRelX = Math.Clamp(anchorX + (int)deltaX, (int)(window.Width * 0.22), (int)(window.Width * 0.60));
        var endRelY = Math.Clamp(anchorY + (int)deltaY, (int)(window.Height * 0.18), (int)(window.Height * 0.82));
        var (endX, endY) = window.ToScreen(endRelX, endRelY);
        var beforeX = centerX;
        var beforeY = centerY;

        var ticket = ctx.Input.Drag(
            startX, startY, endX, endY, ClickIntent.InteractUi,
            $"pan atlas for {nodeName}",
            expectResolved: () => _getSnapshot()?.AtlasPanel.AtlasCanvasChildRect(uiIndex) is { } moved
                && (Math.Abs(moved.CenterX - beforeX) > 4 || Math.Abs(moved.CenterY - beforeY) > 4),
            timeoutMs: 2000);
        if (ticket.Accepted)
        {
            _nodePanAttempts++;
            _lastActionAt = BotMonotonicClock.Now;
            _nodePanAnchorCandidate = 0;
            _nodePanHoverStartedAt = TimeSpan.MinValue;
            _nodeHoverUiIndex = -1;
            _nodeHoverStartedAt = TimeSpan.MinValue;
            Status = $"panning {nodeName} into safe atlas viewport ({_nodePanAttempts}/{MaxAtlasPanAttempts})";
            Diagnostics.EventLog.Emit(
                "MapDevice", "map-device.atlas-pan-requested",
                Diagnostics.EventSeverity.Info, Status,
                new Dictionary<string, object?>
                {
                    ["targetMap"] = nodeName,
                    ["uiIndex"] = uiIndex,
                    ["fromX"] = startX,
                    ["fromY"] = startY,
                    ["toX"] = endX,
                    ["toY"] = endY,
                });
        }
        return Result.InProgress;
    }

    internal static bool AtlasNodeInSafeViewport(
        float centerX, float centerY, int windowWidth, int windowHeight)
        => windowWidth > 0 && windowHeight > 0
        && centerX >= windowWidth * 0.18
        && centerX <= windowWidth * 0.62
        && centerY >= windowHeight * 0.14
        && centerY <= windowHeight * 0.82;

    /// <summary>Verify the selected node matches the recipe — only enforced when a scarab loadout
    /// is required (the stacked-deck safety check; general mapping carries no scarab recipe).</summary>
    private bool TargetMapSelected(
        BehaviorContext ctx, AtlasPanelView atlas, out string error)
    {
        error = string.Empty;
        if (_payloadSource is not (PayloadSource.AtlasStorage or PayloadSource.InventoryNormalMap))
            return true;
        if (ctx.Strategy is null)
        {
            error = "no active strategy available for atlas-node verification";
            return false;
        }
        var expected = ctx.Strategy.Supply.Map.TargetMapName.Trim();
        var actual = atlas.SelectedMapName();
        if (actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) return true;
        error = $"wrong atlas node selected: expected '{expected}', observed '{actual}'";
        return false;
    }

    private static bool StackedDeckScarabLoadoutReady(
        BehaviorContext ctx, AtlasPanelView atlas, out string error)
    {
        error = string.Empty;
        if (ctx.Strategy is not { Supply.Scarabs.Count: > 0 } strategy) return true;
        var expected = Math.Clamp(strategy.Supply.Scarabs.Sum(s => Math.Max(0, s.CountPerMap)), 0, 5);
        var occupied = 0;
        for (var slot = 1; slot <= 5; slot++)
            if (atlas.DeviceSlot(slot) is { IsOccupied: true }) occupied++;
        if (occupied >= expected) return true;
        error = $"scarab loadout incomplete: {occupied}/{expected} scarab slots occupied";
        return false;
    }

    /// <summary>
    /// Stage a positively identified Blight-ravaged map through the same right-click flow
    /// proven for Simulacrum. All map items share the ordinary MapKey metadata path, so the
    /// selector requires the current-build <c>is_uber_blighted_map</c> item stat. Multiple
    /// eligible supplies are safe; the top-left inventory candidate is selected
    /// deterministically while unrelated maps remain untouched.
    /// </summary>
    private Result TickSelectCarriedMap(BehaviorContext ctx)
    {
        var atlas = ctx.Snapshot.AtlasPanel;
        if (!atlas.IsVisible)
            return Fail("atlas panel closed unexpectedly during carried-map insert");
        if (atlas.IsDevicePanelVisible()
            && atlas.DeviceSlot(0) is { IsOccupied: true })
        {
            // Do not tap Inventory here. In PoE that hotkey can close the atlas/device
            // surface along with the inventory, which turns a successful insert into a
            // 30-second retry. Activate owns a short, positively confirmed button settle.
            return Advance(Phase.Activate, "carried map staged - settling activate button");
        }

        if ((BotMonotonicClock.Now - _lastActionAt).TotalMilliseconds < InsertSettleMs)
        {
            Status = "waiting for device to update after carried-map insert";
            return Result.InProgress;
        }

        var inventory = ctx.Snapshot.Inventory;
        if (!inventory.IsOpen)
        {
            var open = ctx.Input.VerifiedTapKey(
                VK_INVENTORY, ClickIntent.InteractUi,
                "open inventory for carried map",
                expectResolved: () => _getSnapshot()?.Inventory.IsOpen ?? false,
                timeoutMs: 1500);
            if (open.Accepted)
                Status = "opening inventory for carried map";
            return Result.InProgress;
        }

        var candidates = inventory.Items
            .Where(item => InventoryView.IsMap(item) && item.Rect is not null)
            .ToArray();
        if (candidates.Length == 0)
            return Fail("no carried MapKey item found");

        var eligible = candidates
            .Where(item => InventoryView.IsBlightRavagedMap(item))
            .OrderBy(item => item.Rect!.Value.Y)
            .ThenBy(item => item.Rect!.Value.X)
            .ToArray();
        if (eligible.Length == 0)
            return Fail($"{candidates.Length} carried maps are visible, but none has the is_uber_blighted_map stat");
        if (_clickAttempts >= MaxClickAttempts)
            return Fail($"failed to stage carried map after {MaxClickAttempts} right-clicks");

        var carried = eligible[0];
        var rect = carried.Rect!.Value;
        var (sx, sy) = ctx.Snapshot.Window.ToScreen((int)rect.CenterX, (int)rect.CenterY);
        var ticket = ctx.Input.RightClick(
            sx, sy, ClickIntent.InteractUi, "insert carried Blight map",
            expectResolved: () => _getSnapshot()?.AtlasPanel is { } liveAtlas
                && liveAtlas.IsDevicePanelVisible()
                && liveAtlas.DeviceSlot(0) is { IsOccupied: true },
            timeoutMs: 2500);
        if (ticket.Accepted)
        {
            _clickAttempts++;
            _lastActionAt = BotMonotonicClock.Now;
            Status = $"right-clicked carried map ({_clickAttempts}/{MaxClickAttempts})";
            BubblesBot.Bot.Diagnostics.EventLog.Emit(
                "blight", "blight.device-insert-requested",
                BubblesBot.Bot.Diagnostics.EventSeverity.Info,
                "right-clicked a positively identified Blight-ravaged map from player inventory",
                new Dictionary<string, object?>
                {
                    ["source"] = "player-inventory",
                    ["itemPath"] = carried.Path,
                    ["mapCandidateCount"] = candidates.Length,
                    ["eligibleBlightRavagedCount"] = eligible.Length,
                    ["identityStatId"] = InventoryView.UberBlightedMapStatId,
                });
        }
        return Result.InProgress;
    }

    /// <summary>Stage an exact white (Normal rarity) map carried in player inventory.</summary>
    private Result TickSelectCarriedNormalMap(BehaviorContext ctx)
    {
        var atlas = ctx.Snapshot.AtlasPanel;
        if (!atlas.IsVisible)
            return Fail("atlas panel closed unexpectedly during carried normal-map insert");
        if (atlas.IsDevicePanelVisible() && atlas.DeviceSlot(0) is { IsOccupied: true })
        {
            if (!TargetMapSelected(ctx, atlas, out var stagedError)) return Fail(stagedError);
            return Advance(Phase.Activate, "carried normal map staged - settling activate button");
        }

        // Generic T16 keys do not encode a map name. The selected Atlas node is the map
        // identity, so prove Jungle Valley before consuming anything.
        if (!atlas.IsDevicePanelVisible())
            return TickSelectAtlasNode(ctx);
        if (!TargetMapSelected(ctx, atlas, out var selectionError))
            return TickSelectAtlasNode(ctx);

        if ((BotMonotonicClock.Now - _lastActionAt).TotalMilliseconds < InsertSettleMs)
        {
            Status = "waiting for device to update after carried normal-map insert";
            return Result.InProgress;
        }

        var inventory = ctx.Snapshot.Inventory;
        if (!inventory.IsOpen)
        {
            var open = ctx.Input.VerifiedTapKey(
                VK_INVENTORY, ClickIntent.InteractUi,
                "open inventory for carried normal map",
                expectResolved: () => _getSnapshot()?.Inventory.IsOpen ?? false,
                timeoutMs: 1500);
            if (open.Accepted) Status = "opening inventory for carried normal map";
            return Result.InProgress;
        }

        var targetName = ctx.Strategy?.Supply.Map.TargetMapName.Trim() ?? string.Empty;
        if (targetName.Length == 0) return Fail("active strategy has no target carried-map name");
        var visibleMaps = inventory.Items.Where(item => InventoryView.IsMap(item)).ToArray();
        var eligible = visibleMaps
            .Where(item => item.Rect is not null && InventoryView.IsNormalTierMap(item, 16))
            .OrderBy(item => item.Rect!.Value.Y)
            .ThenBy(item => item.Rect!.Value.X)
            .ToArray();
        if (eligible.Length == 0)
            return Fail($"no unrolled Normal-rarity T16 key in player inventory ({visibleMaps.Length} map items visible)");
        if (_clickAttempts >= MaxClickAttempts)
            return Fail($"failed to stage carried '{targetName}' after {MaxClickAttempts} right-clicks");

        var carried = eligible[0];
        var rect = carried.Rect!.Value;
        var (sx, sy) = ctx.Snapshot.Window.ToScreen((int)rect.CenterX, (int)rect.CenterY);
        var ticket = ctx.Input.ModifierClick(
            sx, sy, [VK_LCONTROL], ClickIntent.InteractUi, $"insert carried T16 key for {targetName}",
            expectResolved: () => _getSnapshot()?.AtlasPanel is { } liveAtlas
                && liveAtlas.IsDevicePanelVisible()
                && liveAtlas.DeviceSlot(0) is { IsOccupied: true },
            timeoutMs: 2500);
        if (ticket.Accepted)
        {
            _clickAttempts++;
            _lastActionAt = BotMonotonicClock.Now;
            Status = $"ctrl-clicked carried T16 key for {targetName} ({_clickAttempts}/{MaxClickAttempts})";
            BubblesBot.Bot.Diagnostics.EventLog.Emit(
                "maprun", "maprun.carried-map-insert-requested",
                BubblesBot.Bot.Diagnostics.EventSeverity.Info,
                $"Ctrl-clicked an unrolled Normal-rarity T16 key for confirmed node '{targetName}'",
                new Dictionary<string, object?>
                {
                    ["source"] = "player-inventory",
                    ["itemPath"] = carried.Path,
                    ["baseName"] = carried.BaseName,
                    ["rarity"] = carried.Rarity.ToString(),
                    ["quality"] = carried.Quality,
                    ["eligibleCount"] = eligible.Length,
                });
        }
        return Result.InProgress;
    }

    private Result TickSelectSimulacrum(BehaviorContext ctx)
    {
        var atlas = ctx.Snapshot.AtlasPanel;
        if (!atlas.IsVisible)
            return Fail("atlas panel closed unexpectedly during Simulacrum insert");
        if (atlas.IsDevicePanelVisible()
            && atlas.DeviceSlot(0) is { IsOccupied: true })
        {
            if (ctx.Snapshot.Inventory.IsOpen)
            {
                var close = ctx.Input.VerifiedTapKey(
                    VK_INVENTORY, ClickIntent.InteractUi,
                    "close inventory after staging Simulacrum",
                    expectResolved: () => !(_getSnapshot()?.Inventory.IsOpen ?? true),
                    timeoutMs: 1500);
                if (close.Accepted)
                    Status = "closing inventory before activate";
                return Result.InProgress;
            }
            return Advance(Phase.Activate, "Simulacrum staged - clicking activate");
        }

        if ((BotMonotonicClock.Now - _lastActionAt).TotalMilliseconds < InsertSettleMs)
        {
            Status = "waiting for device to update after Simulacrum insert";
            return Result.InProgress;
        }

        var inventory = ctx.Snapshot.Inventory;
        if (!inventory.IsOpen)
        {
            var open = ctx.Input.VerifiedTapKey(
                VK_INVENTORY, ClickIntent.InteractUi,
                "open inventory for carried Simulacrum",
                expectResolved: () => _getSnapshot()?.Inventory.IsOpen ?? false,
                timeoutMs: 1500);
            if (open.Accepted)
                Status = "opening inventory for carried Simulacrum";
            return Result.InProgress;
        }

        InventoryView.Item? carried = null;
        foreach (var item in inventory.Items)
        {
            if (!item.Path.Contains(
                    InventoryView.SimulacrumPathFragment,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            if (item.Rect is null) continue;
            carried = item;
            break;
        }
        if (_clickAttempts >= MaxClickAttempts)
            return Fail($"failed to stage Simulacrum after {MaxClickAttempts} right-clicks");

        if (carried is { } carriedItem)
        {
            var carriedRect = carriedItem.Rect!.Value;
            var (ix, iy) = ctx.Snapshot.Window.ToScreen(
                (int)carriedRect.CenterX, (int)carriedRect.CenterY);
            var inventoryTicket = ctx.Input.RightClick(
                ix, iy, ClickIntent.InteractUi, "insert carried Simulacrum",
                expectResolved: () => _getSnapshot()?.AtlasPanel is { } liveAtlas
                    && liveAtlas.IsDevicePanelVisible()
                    && liveAtlas.DeviceSlot(0) is { IsOccupied: true },
                timeoutMs: 2500);
            if (inventoryTicket.Accepted)
            {
                _clickAttempts++;
                _lastActionAt = BotMonotonicClock.Now;
                Status = $"right-clicked carried Simulacrum ({_clickAttempts}/{MaxClickAttempts})";
                BubblesBot.Bot.Diagnostics.EventLog.Emit(
                    "simulacrum", "simulacrum.device-insert-requested",
                    BubblesBot.Bot.Diagnostics.EventSeverity.Info,
                    "right-clicked carried Simulacrum from player inventory",
                    new Dictionary<string, object?>
                    {
                        ["source"] = "player-inventory",
                        ["itemPath"] = carriedItem.Path,
                    });
            }
            return Result.InProgress;
        }

        // Fallback for direct atlas storage when no carried item remains.
        var target = atlas.StoredItems().FirstOrDefault(item => item.Path.Contains(
            InventoryView.SimulacrumPathFragment, StringComparison.OrdinalIgnoreCase));
        if (target is null) return Fail("no Simulacrum in player inventory or atlas-side storage");
        var rect = target.Rect;
        var (sx, sy) = ctx.Snapshot.Window.ToScreen(
            (int)rect.CenterX, (int)rect.CenterY);
        var ticket = ctx.Input.RightClick(
            sx, sy, ClickIntent.InteractUi, "insert Simulacrum",
            expectResolved: () => _getSnapshot()?.AtlasPanel is { } liveAtlas
                && liveAtlas.IsDevicePanelVisible()
                && liveAtlas.DeviceSlot(0) is { IsOccupied: true },
            timeoutMs: 2500);
        if (ticket.Accepted)
        {
            _clickAttempts++;
            _lastActionAt = BotMonotonicClock.Now;
            Status = $"right-clicked Simulacrum ({_clickAttempts}/{MaxClickAttempts})";
            BubblesBot.Bot.Diagnostics.EventLog.Emit(
                "simulacrum", "simulacrum.device-insert-requested",
                BubblesBot.Bot.Diagnostics.EventSeverity.Info,
                "right-clicked Simulacrum from atlas-side storage",
                new Dictionary<string, object?>
                {
                    ["source"] = "atlas-storage",
                    ["storageIndex"] = target.Index,
                    ["itemPath"] = target.Path,
                });
        }
        return Result.InProgress;
    }

    private Result TickActivate(BehaviorContext ctx)
    {
        var atlas = ctx.Snapshot.AtlasPanel;

        if (_payloadSource == PayloadSource.AtlasStorage
            && atlas.IsVisible && atlas.IsDevicePanelVisible())
        {
            if (!TargetMapSelected(ctx, atlas, out var selectionError))
                return Fail(selectionError);
            if (!StackedDeckScarabLoadoutReady(ctx, atlas, out var loadoutError))
                return Fail(loadoutError);
        }

        // If portals already exist, the activate already happened.
        if (FindBlightPortal(ctx) is not null)
        {
            _clickAttempts = 0;
            return Advance(Phase.WaitForPortals, "portals detected — confirming spawn");
        }

        // Atlas closed = PoE auto-closed it after a successful activate click. The portal
        // entities take a tick or two to spawn after the close, so we advance to WaitForPortals
        // (which has its own dedicated timeout) instead of failing here. If the atlas closed
        // BEFORE we ever clicked (Esc keypress, panel-collision with another window), that
        // shows up as _clickAttempts == 0 — only THEN is it a real failure.
        if (!atlas.IsVisible)
        {
            if (_clickAttempts == 0) return Fail("atlas closed before activate clicked");
            return Advance(Phase.WaitForPortals, "atlas closed post-activate — waiting for portals");
        }

        var btn = atlas.ActivateButtonRect();
        if (btn is null)
        {
            _activateReadySince = TimeSpan.MinValue;
            Status = "activate button not found yet";
            return Result.InProgress;
        }
        if (!atlas.IsActivateReady())
        {
            _activateReadySince = TimeSpan.MinValue;
            Status = "waiting for enabled activate label";
            return Result.InProgress;
        }

        if (_activateReadySince == TimeSpan.MinValue)
        {
            _activateReadySince = BotMonotonicClock.Now;
            Status = "activate enabled - settling UI";
            return Result.InProgress;
        }
        var readyFor = (BotMonotonicClock.Now - _activateReadySince).TotalMilliseconds;
        if (readyFor < ActivateReadySettleMs)
        {
            Status = $"activate enabled - settling UI ({readyFor:F0}/{ActivateReadySettleMs}ms)";
            return Result.InProgress;
        }
        if (!ctx.Input.IsIdle)
        {
            Status = "activate ready - waiting for prior input confirmation";
            return Result.InProgress;
        }

        if (_clickAttempts >= MaxClickAttempts)
            return Fail($"activate clicked {MaxClickAttempts}× without portal spawn");

        var (sx, sy) = ctx.Snapshot.Window.ToScreen((int)btn.Value.CenterX, (int)btn.Value.CenterY);
        var ticket = ctx.Input.Click(sx, sy, ClickIntent.InteractUi, "activate map",
            expectResolved: () => FindBlightPortal(ctx) is not null, timeoutMs: 3000);
        if (ticket.Accepted)
        {
            _clickAttempts++;
            _lastActionAt = BotMonotonicClock.Now;
            Status = $"clicked activate ({_clickAttempts}/{MaxClickAttempts})";
            BubblesBot.Bot.Diagnostics.EventLog.Log("MapDevice", $"activate click sent at ({sx},{sy})");
        }
        return Result.InProgress;
    }

    private Result TickWaitForPortals(BehaviorContext ctx)
    {
        var portal = FindBlightPortal(ctx);
        if (portal is null)
        {
            Status = "waiting for portal spawn";
            return Result.InProgress;
        }
        // The carried-map inventory stayed open intentionally while Activate settled. Once
        // portals positively exist, it is safe to close only that panel before world clicks.
        if (ctx.Snapshot.Inventory.IsOpen)
        {
            var close = ctx.Input.VerifiedTapKey(
                VK_INVENTORY, ClickIntent.InteractUi,
                "close inventory after map activation",
                expectResolved: () => !(_getSnapshot()?.Inventory.IsOpen ?? true),
                timeoutMs: 1200);
            if (close.Accepted) Status = "portals spawned - closing inventory";
            return Result.InProgress;
        }
        _portalEntityId = portal.Id;
        return Advance(Phase.EnterPortal, $"portal id={portal.Id} — entering");
    }

    private Result TickEnterPortal(BehaviorContext ctx)
    {
        // Area-change is the success signal — the bot's BotApp.Tick area-change handler
        // calls Cancel/Reset on the calling mode, which will pull this system back to Idle
        // via Cancel(). Until that fires we keep walking + clicking the portal.
        if (ctx.Entities is null || ctx.Live is null) return Result.InProgress;

        EntityCache.Entry? portal = null;
        if (_portalEntityId != 0
            && ctx.Entities.Entries.TryGetValue(_portalEntityId, out var p)
            && !p.IsStale)
            portal = p;
        portal ??= FindBlightPortal(ctx);
        if (portal is null)
        {
            Status = "waiting for a fresh portal entity before entering";
            return Result.InProgress;
        }

        // While a verified click is pending, do not walk toward or click another portal.
        // If it times out, rotate to the next newly spawned portal. Some hideout layouts
        // overlap one portal label with scenery/device geometry; retrying that same entity
        // for the entire phase wastes the already-created map.
        if (!ctx.Input.IsIdle)
        {
            Status = $"waiting for portal {_portalEntityId} transition (attempt {_clickAttempts})";
            return Result.InProgress;
        }
        if (_clickAttempts > 0)
        {
            var alternatives = FindBlightPortals(ctx);
            if (alternatives.Count > 1)
            {
                var next = alternatives[_clickAttempts % alternatives.Count];
                if (next.Id != _portalEntityId)
                {
                    portal = next;
                    _portalEntityId = portal.Id;
                    _portalInRangeSince = TimeSpan.MinValue;
                }
            }
        }

        var dist = Distance(ctx.Live.Value.GridPosition, portal.GridPosition);
        var portalClickRange = MapPortalEntryPolicy.ClickRange(
            ctx.Settings.InteractionRangeGrid);
        if (dist > portalClickRange)
        {
            _portalInRangeSince = TimeSpan.MinValue;
            // Inline approach — we don't share the FollowPath state with NavigateToDevice
            // because its goal selector is hard-wired to the device entity.
            var goal = portal.GridPosition;
            _movement.WalkToward(goal, new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live));
            Status = $"walking to portal (dist={dist:F0})";
            return Result.InProgress;
        }

        _movement.Release();
        if (_portalInRangeSince == TimeSpan.MinValue)
        {
            _portalInRangeSince = BotMonotonicClock.Now;
            Status = $"in portal range - settling {_portalEntityId}";
            return Result.InProgress;
        }
        var settledFor = (BotMonotonicClock.Now - _portalInRangeSince).TotalMilliseconds;
        if (settledFor < PortalInRangeSettleMs)
        {
            Status = $"settling portal {_portalEntityId} ({settledFor:F0}/{PortalInRangeSettleMs}ms)";
            return Result.InProgress;
        }
        // Portal ground labels are the same click surface used by the proven generic area-
        // transition behavior. Prefer that rectangle; entity-bounds projection remains a
        // fallback for the brief frame before the label enters the snapshot.
        var clickPoint = ResolvePortalClickPoint(ctx, portal);
        if (clickPoint is null) { Status = "no portal click point"; return Result.InProgress; }

        var startAreaHash = ctx.Snapshot.AreaHash;
        var ticket = ctx.Input.Click(clickPoint.Value.X, clickPoint.Value.Y,
            ClickIntent.InteractWorld, "enter map portal",
            expectResolved: () => _getSnapshot() is { } s && s.AreaHash != 0 && s.AreaHash != startAreaHash,
            timeoutMs: PortalClickTimeoutMs);
        if (ticket.Accepted)
        {
            _clickAttempts++;
            _lastActionAt = BotMonotonicClock.Now;
            Status = $"clicked portal {_portalEntityId} attempt {_clickAttempts} - waiting for area change";
        }
        return Result.InProgress;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private Result Advance(Phase next, string status)
    {
        BubblesBot.Bot.Diagnostics.EventLog.Log("MapDevice", $"phase {CurrentPhase} → {next}: {status}");
        CurrentPhase     = next;
        _phaseStartedAt  = BotMonotonicClock.Now;
        _lastActionAt    = TimeSpan.Zero;   // allow immediate first action of next phase
        _clickAttempts   = 0;
        _activateReadySince = TimeSpan.MinValue;
        _portalInRangeSince = TimeSpan.MinValue;
        Status           = status;
        return Result.InProgress;
    }

    private Result Fail(string reason)
    {
        BubblesBot.Bot.Diagnostics.EventLog.Log("MapDevice", $"FAILED: {reason}");
        CurrentPhase = Phase.Failed;
        Status = reason;
        _movement.Release();
        return Result.Failed;
    }

    /// <summary>Goal for the FollowPath approach. Refreshed each tick from the live cache.</summary>
    private Vector2i? GetDeviceGoal(BehaviorContext ctx)
    {
        if (_deviceAccessGoal is { } access) return access;
        var d = FindMapDevice(ctx);
        return d?.GridPosition;
    }

    private static EntityCache.Entry? FindMapDevice(BehaviorContext ctx)
    {
        if (ctx.Entities is null) return null;
        foreach (var e in ctx.Entities.Entries.Values)
        {
            if (e.IsStale) continue;
            if (string.IsNullOrEmpty(e.Path)) continue;
            // AutoExile primary signal: RenderName == "Map Device". Fallback: path contains
            // "MappingDevice". RenderName comes off the entity's Render component already
            // cached on the Entry as Name.
            if (e.Name == "Map Device") return e;
            if (e.Path.Contains("MappingDevice", StringComparison.OrdinalIgnoreCase)) return e;
        }
        return null;
    }

    private EntityCache.Entry? FindBlightPortal(BehaviorContext ctx)
    {
        if (ctx.Entities is null || ctx.Live is null) return null;
        var p = ctx.Live.Value.GridPosition;
        EntityCache.Entry? best = null;
        long bestD2 = long.MaxValue;
        foreach (var e in ctx.Entities.Entries.Values)
        {
            if (e.IsStale) continue;
            if (string.IsNullOrEmpty(e.Path)) continue;
            // Hideout-spawned blight portal — actual entity path varies between leagues.
            // Match on "Portal" anywhere in the path; restrict to the player's bubble.
            if (!e.Path.Contains("Portal", StringComparison.OrdinalIgnoreCase)) continue;
            // Skip the in-map BlightPortal lane spawners.
            if (e.Path.Contains("/BlightPortal", StringComparison.Ordinal)) continue;
            // Skip portals that were ALREADY in the area when this flow started — those are
            // leftovers from the previous map and would lead the bot back into it.
            if (_preFlowPortalIds.Contains(e.Id)) continue;
            long dx = e.GridPosition.X - p.X;
            long dy = e.GridPosition.Y - p.Y;
            var d2 = dx * dx + dy * dy;
            if (d2 < bestD2) { bestD2 = d2; best = e; }
        }
        return best;
    }

    private IReadOnlyList<EntityCache.Entry> FindBlightPortals(BehaviorContext ctx)
    {
        if (ctx.Entities is null || ctx.Live is null) return Array.Empty<EntityCache.Entry>();
        var p = ctx.Live.Value.GridPosition;
        var candidates = new List<(EntityCache.Entry Portal, long DistanceSquared)>();
        foreach (var e in ctx.Entities.Entries.Values)
        {
            if (e.IsStale || string.IsNullOrEmpty(e.Path)) continue;
            if (!e.Path.Contains("Portal", StringComparison.OrdinalIgnoreCase)) continue;
            if (e.Path.Contains("/BlightPortal", StringComparison.Ordinal)) continue;
            if (_preFlowPortalIds.Contains(e.Id)) continue;
            long dx = e.GridPosition.X - p.X;
            long dy = e.GridPosition.Y - p.Y;
            candidates.Add((e, dx * dx + dy * dy));
        }
        return candidates
            .OrderBy(candidate => candidate.DistanceSquared)
            .ThenBy(candidate => candidate.Portal.Id)
            .Select(candidate => candidate.Portal)
            .ToArray();
    }

    private static (int X, int Y)? ResolvePortalClickPoint(
        BehaviorContext ctx, EntityCache.Entry portal)
    {
        var label = ctx.Snapshot.GroundLabels.FirstOrDefault(candidate =>
            candidate.EntityId == portal.Id
            && candidate.IsLabelVisible
            && candidate.IsRectOnScreen
            && candidate.LabelRect is not null);
        if (label?.LabelRect is { } rect)
        {
            var (sx, sy) = ctx.Snapshot.Window.ToScreen(
                (int)rect.CenterX, (int)rect.CenterY);
            return (sx, sy);
        }
        return ResolveEntityClickPoint(ctx, portal);
    }

    /// <summary>
    /// Project an entity's bounds-center to absolute screen coords (mirrors the AutoExile
    /// click formula). Falls back to grid-position-at-player-Z when the bounds projection
    /// lands off-screen — same two-stage approach as <c>InteractWorldEntity</c>.
    /// </summary>
    private static (int X, int Y)? ResolveEntityClickPoint(BehaviorContext ctx, EntityCache.Entry target)
    {
        var cam = ctx.Snapshot.Camera;
        if (!cam.IsValid || ctx.Live is null) return null;
        var w = ctx.Snapshot.Window;
        bool OnScreen((float X, float Y)? p) => p is { } v && v.X >= 0 && v.Y >= 0 && v.X < w.Width && v.Y < w.Height;

        (float X, float Y)? center = null;
        if (target.RenderCompAddr != 0)
        {
            var reader = ctx.Snapshot.Reader;
            if (reader.TryReadStruct<Vector3>(target.RenderCompAddr + KnownOffsets.RenderComponent.Pos, out var rPos)
             && reader.TryReadStruct<Vector3>(target.RenderCompAddr + KnownOffsets.RenderComponent.Bounds, out var rBounds))
            {
                var centerWorld = new Vector3 { X = rPos.X + rBounds.X * 0.5f, Y = rPos.Y + rBounds.Y * 0.5f, Z = rPos.Z + rBounds.Z * 0.5f };
                center = cam.WorldToScreen(centerWorld);
            }
        }
        if (!OnScreen(center))
        {
            center = cam.GridToScreenAtPlayerZ(target.GridPosition, ctx.Live.Value.WorldPosition.Z);
            if (!OnScreen(center)) return null;
        }
        var (sx, sy) = w.ToScreen((int)center!.Value.X, (int)center.Value.Y);
        return (sx, sy);
    }

    private static float Distance(Vector2i a, Vector2i b)
    {
        var dx = (float)(a.X - b.X);
        var dy = (float)(a.Y - b.Y);
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private readonly record struct StoredItemRef(int Index, ElementGeometry.Rect Rect);
}

/// <summary>Chooses a within-interaction-range point with maximum clearance from old portals.</summary>
public static class MapDeviceAccessPolicy
{
    public static Vector2i? Choose(
        Vector2i device,
        Vector2i player,
        IReadOnlyList<Vector2i> oldPortals,
        float interactionRange,
        int rank = 0)
    {
        if (oldPortals.Count == 0) return null;
        var radius = Math.Clamp(interactionRange * 0.75f, 10f, 22f);
        var candidates = new List<(Vector2i Position, float Clearance, float Travel)>();
        for (var i = 0; i < 16; i++)
        {
            var angle = i * MathF.Tau / 16f;
            var candidate = new Vector2i
            {
                X = device.X + (int)MathF.Round(MathF.Cos(angle) * radius),
                Y = device.Y + (int)MathF.Round(MathF.Sin(angle) * radius),
            };
            var clearance = oldPortals.Min(portal => Distance(candidate, portal));
            var travel = Distance(player, candidate);
            candidates.Add((candidate, clearance, travel));
        }
        return candidates
            .OrderByDescending(candidate => candidate.Clearance)
            .ThenBy(candidate => candidate.Travel)
            .ThenBy(candidate => candidate.Position.X)
            .ThenBy(candidate => candidate.Position.Y)
            .ElementAt(Math.Clamp(rank, 0, candidates.Count - 1))
            .Position;
    }

    private static float Distance(Vector2i a, Vector2i b)
    {
        var dx = (float)(a.X - b.X);
        var dy = (float)(a.Y - b.Y);
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}

/// <summary>Portal collision rings stop walking slightly outside generic interaction range.</summary>
public static class MapPortalEntryPolicy
{
    public static float ClickRange(float interactionRange)
        => Math.Clamp(interactionRange + 12f, interactionRange, 45f);
}
