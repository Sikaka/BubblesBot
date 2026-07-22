using BubblesBot.Bot.Behaviors;
using BubblesBot.Bot.Input;
using BubblesBot.Bot.Strategies;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Systems;

/// <summary>
/// Discovers the four Guardian nodes and their Maven-witness state from exact Atlas tooltips.
/// It never clicks a node. A discovered UI index is converted to a data index and registered
/// only after hover ancestry and the Guardian map name agree.
/// </summary>
public sealed class GuardianAtlasScanner
{
    public enum Result { InProgress, Succeeded, Failed }

    private const int AtlasKeyVk = 0x47; // default G
    private const int HoverSettleMs = 170;
    private const int PanSettleMs = 650;
    private static readonly (double X, double Y)[] PanMoves =
    [
        ( 0.18,  0.00), // left atlas sector
        (-0.36,  0.00), // right atlas sector
        ( 0.18,  0.22), // upper sector
        ( 0.00, -0.44), // lower sector
        ( 0.18,  0.44), // upper-left
        (-0.36,  0.00), // upper-right
        ( 0.00, -0.44), // lower-right
        ( 0.36,  0.00), // lower-left
    ];
    private static readonly (double X, double Y)[] PanAnchors = BuildPanAnchors();
    private int _index = MapDeviceSystem.CurrentAtlasNodeUiPrefix;
    private int _hoveredIndex = -1;
    private TimeSpan _hoveredAt = TimeSpan.MinValue;
    private TimeSpan _startedAt = TimeSpan.MinValue;
    private TimeSpan _panHoverAt = TimeSpan.MinValue;
    private TimeSpan _panSettleUntil = TimeSpan.MinValue;
    private int _panPass;
    private int _panAnchorCandidate;
    private int _knownTargetIndex;
    private int _knownPanAttempts;
    private bool _knownPanActive;
    private readonly Dictionary<string, GuardianWitnessStatus> _states =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<GameSnapshot?> _getSnapshot;

    public GuardianAtlasScanner(Func<GameSnapshot?> getSnapshot) => _getSnapshot = getSnapshot;

    public string Status { get; private set; } = "idle";
    public IReadOnlyDictionary<string, GuardianWitnessStatus> States => _states;

    public void Start()
    {
        _index = MapDeviceSystem.CurrentAtlasNodeUiPrefix;
        _hoveredIndex = -1;
        _hoveredAt = TimeSpan.MinValue;
        _startedAt = BotMonotonicClock.Now;
        _panHoverAt = TimeSpan.MinValue;
        _panSettleUntil = TimeSpan.MinValue;
        _panPass = 0;
        _panAnchorCandidate = 0;
        _knownTargetIndex = 0;
        _knownPanAttempts = 0;
        _knownPanActive = false;
        _states.Clear();
        Status = "opening Atlas for Guardian witness scan";
    }

    public Result Tick(BehaviorContext ctx)
    {
        if (_startedAt == TimeSpan.MinValue) Start();
        var atlas = ctx.Snapshot.AtlasPanel;
        if (!atlas.IsVisible)
        {
            if ((BotMonotonicClock.Now - _startedAt).TotalSeconds > 5)
                return Fail("Atlas did not open on the configured G key");
            ctx.Input.VerifiedTapKey(
                AtlasKeyVk, ClickIntent.InteractUi, "open Atlas for Guardian scan",
                expectResolved: () => _getSnapshot()?.AtlasPanel.IsVisible ?? false,
                timeoutMs: 2000);
            return Result.InProgress;
        }

        if (_states.Count == GuardianRotationPolicy.Maps.Count)
        {
            Status = "all four Guardian witness states and node indices verified";
            return Result.Succeeded;
        }
        if (_panSettleUntil != TimeSpan.MinValue && BotMonotonicClock.Now < _panSettleUntil)
        {
            Status = $"settling Atlas sweep position {_panPass + 1}";
            return Result.InProgress;
        }

        if (TryKnownGuardianTargets(out var knownTargets))
            return TickKnownTargets(ctx, atlas, knownTargets);

        var count = atlas.AtlasCanvasChildCount();
        if (count <= MapDeviceSystem.CurrentAtlasNodeUiPrefix
            && (BotMonotonicClock.Now - _startedAt).TotalSeconds < 5)
        {
            Status = "waiting for Atlas node canvas to hydrate";
            return Result.InProgress;
        }
        while (_index < count)
        {
            var rect = atlas.AtlasCanvasChildRect(_index);
            if (rect is null || !MapDeviceSystem.AtlasNodeInSafeViewport(
                    rect.Value.CenterX, rect.Value.CenterY,
                    ctx.Snapshot.Window.Width, ctx.Snapshot.Window.Height))
            {
                _index++;
                _hoveredIndex = -1;
                continue;
            }

            var point = ctx.Snapshot.Window.ToScreen(
                (int)rect.Value.CenterX, (int)rect.Value.CenterY);
            ctx.Input.HoverAt(point.X, point.Y, CursorPriority.CombatAim);
            if (_hoveredIndex != _index)
            {
                _hoveredIndex = _index;
                _hoveredAt = BotMonotonicClock.Now;
                Status = $"scanning Atlas node {_index + 1}/{count}";
                return Result.InProgress;
            }
            if ((BotMonotonicClock.Now - _hoveredAt).TotalMilliseconds < HoverSettleMs)
                return Result.InProgress;

            var hover = UiHoverView.Read(ctx.Snapshot.Reader, ctx.Snapshot.IngameStateAddress);
            if (atlas.HoverBelongsToAtlasCanvasChild(hover.Element, _index))
            {
                var guardian = GuardianRotationPolicy.ClassifyTooltip(hover.TooltipLines);
                if (guardian.WitnessStatus != GuardianWitnessStatus.Unknown)
                {
                    _states[guardian.MapName] = guardian.WitnessStatus;
                    AtlasNodeCatalog.Observe(
                        guardian.MapName, _index - MapDeviceSystem.CurrentAtlasNodeUiPrefix);
                    Diagnostics.EventLog.Emit(
                        "guardian-rota", "guardian-rota.atlas-node-observed",
                        Diagnostics.EventSeverity.Info,
                        $"{guardian.MapName}: {guardian.WitnessStatus}",
                        new Dictionary<string, object?>
                        {
                            ["map"] = guardian.MapName,
                            ["witness"] = guardian.WitnessStatus.ToString(),
                            ["atlasDataIndex"] = _index - MapDeviceSystem.CurrentAtlasNodeUiPrefix,
                        });
                }
            }
            _index++;
            _hoveredIndex = -1;
            return Result.InProgress;
        }

        if (_panPass < PanMoves.Length)
            return TickPan(ctx, atlas);
        return Fail($"Atlas sweep found {_states.Count}/4 Guardian nodes after {PanMoves.Length + 1} viewports");
    }

    private Result TickKnownTargets(
        BehaviorContext ctx,
        AtlasPanelView atlas,
        IReadOnlyList<(string Map, int UiIndex)> targets)
    {
        while (_knownTargetIndex < targets.Count
            && _states.ContainsKey(targets[_knownTargetIndex].Map))
            _knownTargetIndex++;
        if (_knownTargetIndex >= targets.Count)
        {
            Status = "all four Guardian witness states and node indices verified";
            return Result.Succeeded;
        }

        var target = targets[_knownTargetIndex];
        var rect = atlas.AtlasCanvasChildRect(target.UiIndex);
        if (rect is null)
            return Fail($"{target.Map} Atlas child {target.UiIndex} has no geometry");
        if (_knownPanActive)
            return TickPanKnownTarget(ctx, atlas, target.Map, target.UiIndex, rect.Value);
        if (!MapDeviceSystem.AtlasNodeInGuardianSheetViewport(
                rect.Value.CenterX, rect.Value.CenterY,
                ctx.Snapshot.Window.Width, ctx.Snapshot.Window.Height))
            return TickPanKnownTarget(ctx, atlas, target.Map, target.UiIndex, rect.Value);

        var point = ctx.Snapshot.Window.ToScreen(
            (int)rect.Value.CenterX, (int)rect.Value.CenterY);
        ctx.Input.HoverAt(point.X, point.Y, CursorPriority.CombatAim);
        if (_hoveredIndex != target.UiIndex)
        {
            _hoveredIndex = target.UiIndex;
            _hoveredAt = BotMonotonicClock.Now;
            Status = $"reading {target.Map} witness state ({_knownTargetIndex + 1}/4)";
            return Result.InProgress;
        }
        var hoverElapsedMs = (BotMonotonicClock.Now - _hoveredAt).TotalMilliseconds;
        if (hoverElapsedMs < HoverSettleMs)
        {
            Status = $"settling {target.Map} hover ({hoverElapsedMs:F0}/{HoverSettleMs}ms; child {target.UiIndex})";
            return Result.InProgress;
        }

        var hover = UiHoverView.Read(ctx.Snapshot.Reader, ctx.Snapshot.IngameStateAddress);
        var guardian = GuardianRotationPolicy.ClassifyTooltip(hover.TooltipLines);
        if (!atlas.HoverBelongsToAtlasCanvasChild(hover.Element, target.UiIndex)
            || !guardian.MapName.Equals(target.Map, StringComparison.OrdinalIgnoreCase))
        {
            // A geometrically in-range Guardian node can still sit under inventory or the
            // map-device overlay. Reposition and re-prove it just like an off-viewport node.
            // Do this immediately after the ordinary hover-settle interval: a covered element
            // can continuously invalidate UIHover and otherwise restart a longer timer forever.
            _hoveredIndex = -1;
            _hoveredAt = TimeSpan.MinValue;
            Status = $"{target.Map} tooltip is covered; repositioning Atlas";
            return TickPanKnownTarget(
                ctx, atlas, target.Map, target.UiIndex, rect.Value);
        }

        _states[target.Map] = guardian.WitnessStatus;
        _knownTargetIndex++;
        _knownPanAttempts = 0;
        _knownPanActive = false;
        _hoveredIndex = -1;
        _hoveredAt = TimeSpan.MinValue;
        Diagnostics.EventLog.Emit(
            "guardian-rota", "guardian-rota.atlas-node-observed",
            Diagnostics.EventSeverity.Info,
            $"{target.Map}: {guardian.WitnessStatus}",
            new Dictionary<string, object?>
            {
                ["map"] = target.Map,
                ["witness"] = guardian.WitnessStatus.ToString(),
                ["atlasDataIndex"] = target.UiIndex - MapDeviceSystem.CurrentAtlasNodeUiPrefix,
            });
        return Result.InProgress;
    }

    private Result TickPanKnownTarget(
        BehaviorContext ctx,
        AtlasPanelView atlas,
        string map,
        int uiIndex,
        ElementGeometry.Rect targetRect)
    {
        _knownPanActive = true;
        if (_knownPanAttempts >= 6)
            return Fail($"failed to position {map} after {_knownPanAttempts} targeted Atlas pans");
        if (_panAnchorCandidate >= PanAnchors.Length)
            return Fail($"could not find Atlas background while positioning {map}");
        var window = ctx.Snapshot.Window;
        var desiredX = window.Width * 0.42;
        var desiredY = window.Height * 0.46;
        var dx = Math.Clamp(desiredX - targetRect.CenterX,
            -window.Width * 0.20, window.Width * 0.20);
        var dy = Math.Clamp(desiredY - targetRect.CenterY,
            -window.Height * 0.25, window.Height * 0.25);
        // Prefer an anchor on the side opposite the requested canvas motion. This keeps
        // the entire drag inside the Atlas canvas instead of clipping it at an edge (which
        // PoE accepts as mouse input but does not treat as a canvas pan).
        var orderedAnchors = PanAnchors
            .OrderBy(candidate =>
            {
                var sx = window.Width * candidate.X;
                var sy = window.Height * candidate.Y;
                var ex = Math.Clamp(sx + dx, window.Width * 0.18, window.Width * 0.62);
                var ey = Math.Clamp(sy + dy, window.Height * 0.14, window.Height * 0.82);
                return Math.Abs((ex - sx) - dx) + Math.Abs((ey - sy) - dy);
            })
            .ToArray();
        var anchor = orderedAnchors[_panAnchorCandidate];
        var startRelX = (int)(window.Width * anchor.X);
        var startRelY = (int)(window.Height * anchor.Y);
        var start = window.ToScreen(startRelX, startRelY);
        ctx.Input.HoverAt(start.X, start.Y, CursorPriority.CombatAim);
        if (_panHoverAt == TimeSpan.MinValue)
        {
            _panHoverAt = BotMonotonicClock.Now;
            Status = $"positioning {map} on the Atlas";
            return Result.InProgress;
        }
        if ((BotMonotonicClock.Now - _panHoverAt).TotalMilliseconds < HoverSettleMs)
            return Result.InProgress;

        var hover = UiHoverView.Read(ctx.Snapshot.Reader, ctx.Snapshot.IngameStateAddress);
        var directChild = atlas.AtlasCanvasDirectChildForHover(hover.Element);
        if (!MapDeviceSystem.IsSafeAtlasPanAnchor(
                directChild, hover.TooltipLines.Count))
        {
            _panAnchorCandidate++;
            _panHoverAt = TimeSpan.MinValue;
            return Result.InProgress;
        }

        var endRelX = Math.Clamp(startRelX + (int)dx,
            (int)(window.Width * 0.18), (int)(window.Width * 0.62));
        var endRelY = Math.Clamp(startRelY + (int)dy,
            (int)(window.Height * 0.14), (int)(window.Height * 0.82));
        var end = window.ToScreen(endRelX, endRelY);
        var beforeX = targetRect.CenterX;
        var beforeY = targetRect.CenterY;
        var ticket = ctx.Input.Drag(
            start.X, start.Y, end.X, end.Y,
            ClickIntent.InteractUi, $"position {map} Atlas node",
            expectResolved: () => _getSnapshot()?.AtlasPanel.AtlasCanvasChildRect(uiIndex) is { } moved
                && (Math.Abs(moved.CenterX - beforeX) > 4
                    || Math.Abs(moved.CenterY - beforeY) > 4),
            timeoutMs: 2500);
        if (ticket.Accepted)
        {
            _knownPanAttempts++;
            _panAnchorCandidate = 0;
            _panHoverAt = TimeSpan.MinValue;
            _panSettleUntil = BotMonotonicClock.Now + TimeSpan.FromMilliseconds(PanSettleMs);
            _hoveredIndex = -1;
            _knownPanActive = false;
            Status = $"panning {map} {_knownPanAttempts}/6: node=({beforeX:F0},{beforeY:F0}) "
                + $"desired=({desiredX:F0},{desiredY:F0}) drag=({endRelX - startRelX},{endRelY - startRelY})";
            Diagnostics.EventLog.Emit(
                "guardian-rota", "guardian-rota.atlas-target-pan-requested",
                Diagnostics.EventSeverity.Info, Status,
                new Dictionary<string, object?>
                {
                    ["map"] = map,
                    ["uiIndex"] = uiIndex,
                    ["targetBeforeX"] = beforeX,
                    ["targetBeforeY"] = beforeY,
                    ["desiredX"] = desiredX,
                    ["desiredY"] = desiredY,
                    ["dragDeltaX"] = endRelX - startRelX,
                    ["dragDeltaY"] = endRelY - startRelY,
                    ["attempt"] = _knownPanAttempts,
                });
        }
        return Result.InProgress;
    }

    private static bool TryKnownGuardianTargets(
        out IReadOnlyList<(string Map, int UiIndex)> targets)
    {
        var result = new List<(string Map, int UiIndex)>();
        foreach (var map in GuardianRotationPolicy.Maps)
        {
            if (!AtlasNodeCatalog.TryGetDataIndex(map, out var dataIndex))
            {
                targets = [];
                return false;
            }
            result.Add((map, dataIndex + MapDeviceSystem.CurrentAtlasNodeUiPrefix));
        }
        targets = result;
        return true;
    }

    private Result TickPan(BehaviorContext ctx, AtlasPanelView atlas)
    {
        if (_panAnchorCandidate >= PanAnchors.Length)
            return Fail("Atlas sweep could not find an unobstructed canvas background");

        var window = ctx.Snapshot.Window;
        var anchor = PanAnchors[_panAnchorCandidate];
        var startRelX = (int)(window.Width * anchor.X);
        var startRelY = (int)(window.Height * anchor.Y);
        var start = window.ToScreen(startRelX, startRelY);
        ctx.Input.HoverAt(start.X, start.Y, CursorPriority.CombatAim);
        if (_panHoverAt == TimeSpan.MinValue)
        {
            _panHoverAt = BotMonotonicClock.Now;
            Status = $"proving Atlas background for sweep {_panPass + 1}/{PanMoves.Length}";
            return Result.InProgress;
        }
        if ((BotMonotonicClock.Now - _panHoverAt).TotalMilliseconds < HoverSettleMs)
            return Result.InProgress;

        var hover = UiHoverView.Read(ctx.Snapshot.Reader, ctx.Snapshot.IngameStateAddress);
        var directChild = atlas.AtlasCanvasDirectChildForHover(hover.Element);
        // Transparent Atlas background has no element in UIHover on the current client.
        // Accept that exact empty surface only inside our bounded Atlas region; a node or
        // overlay supplies either ancestry or tooltip text and remains rejected. The drag's
        // postcondition still requires actual canvas-node motion.
        if (!MapDeviceSystem.IsSafeAtlasPanAnchor(
                directChild, hover.TooltipLines.Count))
        {
            _panAnchorCandidate++;
            _panHoverAt = TimeSpan.MinValue;
            Status = $"Atlas sweep anchor {_panAnchorCandidate}/{PanAnchors.Length} covered by child {directChild}";
            return Result.InProgress;
        }

        var move = PanMoves[_panPass];
        var endRelX = Math.Clamp(
            startRelX + (int)(window.Width * move.X),
            (int)(window.Width * 0.12), (int)(window.Width * 0.70));
        var endRelY = Math.Clamp(
            startRelY + (int)(window.Height * move.Y),
            (int)(window.Height * 0.12), (int)(window.Height * 0.88));
        var end = window.ToScreen(endRelX, endRelY);
        var representative = Enumerable.Range(
                MapDeviceSystem.CurrentAtlasNodeUiPrefix,
                Math.Max(0, atlas.AtlasCanvasChildCount() - MapDeviceSystem.CurrentAtlasNodeUiPrefix))
            .Select(index => (Index: index, Rect: atlas.AtlasCanvasChildRect(index)))
            .FirstOrDefault(x => x.Rect is not null);
        if (representative.Rect is not { } before)
            return Fail("Atlas sweep has no node geometry to verify canvas motion");

        var representativeIndex = representative.Index;
        var ticket = ctx.Input.Drag(
            start.X, start.Y, end.X, end.Y,
            ClickIntent.InteractUi, $"pan Atlas sweep {_panPass + 1}/{PanMoves.Length}",
            expectResolved: () => _getSnapshot()?.AtlasPanel.AtlasCanvasChildRect(representativeIndex) is { } after
                && (Math.Abs(after.CenterX - before.CenterX) > 4
                    || Math.Abs(after.CenterY - before.CenterY) > 4),
            timeoutMs: 2500);
        if (ticket.Accepted)
        {
            _panPass++;
            _panAnchorCandidate = 0;
            _panHoverAt = TimeSpan.MinValue;
            _panSettleUntil = BotMonotonicClock.Now + TimeSpan.FromMilliseconds(PanSettleMs);
            _index = MapDeviceSystem.CurrentAtlasNodeUiPrefix;
            _hoveredIndex = -1;
            _hoveredAt = TimeSpan.MinValue;
            Status = $"panning Atlas to sweep position {_panPass + 1}";
        }
        return Result.InProgress;
    }

    public void Reset()
    {
        _startedAt = TimeSpan.MinValue;
        _states.Clear();
        Status = "idle";
    }

    private Result Fail(string reason)
    {
        Status = reason;
        return Result.Failed;
    }

    private static (double X, double Y)[] BuildPanAnchors()
    {
        var result = new List<(double X, double Y)>();
        // Dense deterministic search over the atlas portion of the screen. A map-node icon
        // occupies only a small subset; exact hover ancestry still proves background before
        // any drag is dispatched.
        foreach (var y in new[] { 0.18, 0.28, 0.38, 0.48, 0.58, 0.68, 0.78 })
        foreach (var x in new[] { 0.20, 0.27, 0.34, 0.41, 0.48, 0.55, 0.62 })
            result.Add((x, y));
        return result.ToArray();
    }
}
