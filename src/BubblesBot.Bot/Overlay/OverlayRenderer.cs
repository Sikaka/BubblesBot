using System.Numerics;
using BubblesBot.Bot.Overlay.Navigation;
using BubblesBot.Core.Campaign;
using BubblesBot.Core.Game;
using BubblesBot.Core.Knowledge;
using BubblesBot.Core.Pathfinding;
using BubblesBot.Core.Snapshot;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using NumVec2 = System.Numerics.Vector2;

namespace BubblesBot.Bot.Overlay;

/// <summary>
/// Direct2D HUD for the looter test. Draws:
///   • A box around the label the looter is currently targeting (or considered).
/// All D2D resources are created once and reused.
/// </summary>
public sealed class OverlayRenderer : IDisposable
{
    private static readonly Color4 ColorHp     = new(0.18f, 0.75f, 0.18f, 1f);
    private static readonly Color4 ColorHpLow  = new(0.85f, 0.20f, 0.10f, 1f);
    private static readonly Color4 ColorBar    = new(0.12f, 0.12f, 0.12f, 0.80f);
    private static readonly Color4 ColorText   = new(1f,    1f,    1f,    1f);
    private static readonly Color4 ColorDim    = new(0.70f, 0.70f, 0.70f, 1f);
    private static readonly Color4 ColorPanel  = new(0.05f, 0.05f, 0.05f, 0.78f);
    private static readonly Color4 ColorBorder = new(0.30f, 0.30f, 0.30f, 0.90f);
    private static readonly Color4 ColorTarget = new(0.95f, 0.85f, 0.20f, 1f);
    private static readonly Color4 ColorBusy     = new(0.95f, 0.55f, 0.10f, 1f);
    // Normal/magic mobs render in two passes for a "polygon blob" look:
    //   (1) FillGeometry of the merged circle union → defined-edge translucent shape (no
    //       antialias fuzz from N overlapping discs).
    //   (2) Per-mob bright cores → individuals still visible inside the blob.
    // Rare/unique skip the blob and draw as solid filled circles so they pop above it.
    private static readonly Color4 ColorBlob        = new(0.95f, 0.25f, 0.20f, 0.32f);
    private static readonly Color4 ColorBlobOutline = new(1.00f, 0.40f, 0.30f, 0.85f);
    private static readonly Color4 ColorEnemyWhite  = new(1.00f, 0.95f, 0.95f, 0.95f);
    private static readonly Color4 ColorEnemyMagic  = new(0.55f, 0.75f, 1.00f, 0.95f);
    private static readonly Color4 ColorEnemyRare   = new(1.00f, 0.95f, 0.20f, 0.95f);
    private static readonly Color4 ColorEnemyUnique = new(1.00f, 0.55f, 0.10f, 1.00f);
    private static readonly Color4 ColorGroundIt    = new(1.00f, 0.85f, 0.20f, 0.90f);
    private static readonly Color4 ColorPlayer   = new(0.30f, 0.95f, 1.00f, 1.00f);
    private static readonly Color4 ColorPath     = new(0.30f, 0.95f, 0.30f, 0.90f);
    // Mechanic markers (overlay): distinct vivid hues so they stand out against mob blob.
    private static readonly Color4 ColorShrine   = new(0.40f, 1.00f, 0.60f, 1.00f);   // green — buff
    private static readonly Color4 ColorAltar    = new(0.95f, 0.30f, 0.95f, 1.00f);   // magenta — risk
    private static readonly Color4 ColorRitual   = new(1.00f, 0.20f, 0.20f, 1.00f);   // crimson — fight
    // Campaign guidance, color-coded by objective kind so the player learns the mapping at a glance:
    //   quest/other = gold, waypoint = cyan, area exit = green, boss = red.
    private static readonly Color4 ColorGuide         = new(1.00f, 0.82f, 0.25f, 0.95f); // quest / default
    private static readonly Color4 ColorGuideWaypoint = new(0.35f, 0.85f, 1.00f, 1.00f); // waypoint
    private static readonly Color4 ColorGuideExit     = new(0.35f, 0.95f, 0.45f, 1.00f); // area transition
    private static readonly Color4 ColorGuideBoss     = new(1.00f, 0.30f, 0.25f, 1.00f); // boss / arena

    private readonly OverlayWindow _window;
    private TerrainBitmap? _terrainBitmap;

    // Pre-flattened entity dot list, refreshed only when the underlying world snapshot
    // changes. The render path projects these grid coords through the live player position
    // every frame at no per-entity cost — keeps 1000+ monsters cheap on a 144 Hz overlay.
    private enum DotShape { Circle, Triangle, Star, Plus, Diamond, Square }
    private readonly record struct DotEntry(BubblesBot.Core.Game.Vector2i Grid, int BrushIndex, float Radius, bool Filled, DotShape Shape = DotShape.Circle, string? Label = null);
    private readonly List<DotEntry> _entityDots = new();
    private object? _dotsBuiltForSnapshot;

    // Unit-radius geometries for non-circle map icons. Built once, scaled+translated per
    // draw via the render-target transform. Stroke width is divided by the per-draw scale
    // so outlines stay screen-space-constant regardless of icon size.
    private ID2D1PathGeometry? _geoTriangle, _geoStar, _geoDiamond, _geoPlus;


    private ID2D1SolidColorBrush? _bHp, _bHpLow, _bBar, _bText, _bDim, _bPanel, _bBorder, _bTarget, _bBusy;
    private ID2D1SolidColorBrush? _bEnemyWhite, _bEnemyMagic, _bEnemyRare, _bEnemyUnique;
    private ID2D1SolidColorBrush? _bGround, _bPlayer, _bPath;
    private ID2D1SolidColorBrush? _bBlob, _bBlobOutline;
    private ID2D1SolidColorBrush? _bShrine, _bAltar, _bRitual;
    private ID2D1SolidColorBrush? _bGuide, _bGuideWaypoint, _bGuideExit, _bGuideBoss;

    // Polygon blob geometry of normal+magic mob positions in grid space; one FillGeometry
    // per frame. Rebuilt only when the underlying snapshot changes.
    private ID2D1PathGeometry? _mobBlobGeometry;
    private IDWriteTextFormat? _tfNormal, _tfSmall, _tfHeader;
    private bool _resourcesCreated;

    public OverlayRenderer(OverlayWindow window) { _window = window; }

    private void EnsureResources()
    {
        if (_resourcesCreated) return;
        var rt = _window.RenderTarget;
        var dw = _window.DWriteFactory;

        _bHp     = rt.CreateSolidColorBrush(ColorHp);
        _bHpLow  = rt.CreateSolidColorBrush(ColorHpLow);
        _bBar    = rt.CreateSolidColorBrush(ColorBar);
        _bText   = rt.CreateSolidColorBrush(ColorText);
        _bDim    = rt.CreateSolidColorBrush(ColorDim);
        _bPanel  = rt.CreateSolidColorBrush(ColorPanel);
        _bBorder = rt.CreateSolidColorBrush(ColorBorder);
        _bTarget = rt.CreateSolidColorBrush(ColorTarget);
        _bBusy   = rt.CreateSolidColorBrush(ColorBusy);
        _bEnemyWhite  = rt.CreateSolidColorBrush(ColorEnemyWhite);
        _bEnemyMagic  = rt.CreateSolidColorBrush(ColorEnemyMagic);
        _bEnemyRare   = rt.CreateSolidColorBrush(ColorEnemyRare);
        _bEnemyUnique = rt.CreateSolidColorBrush(ColorEnemyUnique);
        _bGround      = rt.CreateSolidColorBrush(ColorGroundIt);
        _bPlayer = rt.CreateSolidColorBrush(ColorPlayer);
        _bPath   = rt.CreateSolidColorBrush(ColorPath);
        _bBlob        = rt.CreateSolidColorBrush(ColorBlob);
        _bBlobOutline = rt.CreateSolidColorBrush(ColorBlobOutline);
        _bShrine = rt.CreateSolidColorBrush(ColorShrine);
        _bAltar  = rt.CreateSolidColorBrush(ColorAltar);
        _bRitual = rt.CreateSolidColorBrush(ColorRitual);
        _bGuide         = rt.CreateSolidColorBrush(ColorGuide);
        _bGuideWaypoint = rt.CreateSolidColorBrush(ColorGuideWaypoint);
        _bGuideExit     = rt.CreateSolidColorBrush(ColorGuideExit);
        _bGuideBoss     = rt.CreateSolidColorBrush(ColorGuideBoss);

        _tfNormal = dw.CreateTextFormat("Consolas", null, FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, 13f, "en-us");
        _tfSmall  = dw.CreateTextFormat("Consolas", null, FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, 11f, "en-us");
        _tfHeader = dw.CreateTextFormat("Consolas", null, FontWeight.Bold,   FontStyle.Normal, FontStretch.Normal, 14f, "en-us");

        // No word-wrap — long lines (e.g. loot diagnostics with paths) get clipped instead
        // of wrapping onto the next entry's row. With wrap enabled the wrapped second line
        // overlaps the next status row, which reads as "text drawn on top of itself."
        _tfNormal.WordWrapping = WordWrapping.NoWrap;
        _tfSmall .WordWrapping = WordWrapping.NoWrap;
        _tfHeader.WordWrapping = WordWrapping.NoWrap;

        _resourcesCreated = true;
    }

    // Present-gating: while PoE isn't the foreground window the overlay must not paint over
    // other apps, and the per-frame UpdateLayeredWindow blit (cost scales with resolution) is
    // wasted. Push exactly one blank frame on the focus-loss edge, then skip drawing entirely
    // until focus returns.
    private bool _blankPushed;

    public void Render(RenderContext ctx)
    {
        if (!_window.IsValid) return;
        EnsureResources();

        var foreground = ctx.Enable?.ForegroundOk ?? true;
        if (!foreground)
        {
            if (_blankPushed) return;
            var rtBlank = _window.RenderTarget;
            rtBlank.BeginDraw();
            rtBlank.Clear(new Color4(0f, 0f, 0f, 0f));
            rtBlank.EndDraw();
            _window.Present();
            _blankPushed = true;
            return;
        }
        _blankPushed = false;

        var rt = _window.RenderTarget;
        rt.BeginDraw();
        // Per-pixel alpha overlay: clear to fully transparent. Anything we don't draw stays
        // alpha=0 and shows the game underneath. Anything we draw at alpha 0..255 composites
        // correctly via UpdateLayeredWindow's per-pixel SRC_OVER blend.
        rt.Clear(new Color4(0f, 0f, 0f, 0f));
        rt.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        try
        {
            DrawMapOverlay(rt, ctx);
            DrawGuidanceWorld(rt, ctx);
            // Corner minimap intentionally not drawn — the in-world DrawMapOverlay paints
            // onto PoE's big-map canvas when it's expanded, and that's the only "minimap"
            // surface we want. Leaving DrawDebugMinimap implementation in place for now in
            // case we want it back as an opt-in dev panel later.
            DrawWorldEntityNames(rt, ctx);
            DrawHpBars(rt, ctx);
            DrawUniqueValueLabels(rt, ctx);
            DrawTargetBox(rt, ctx);
            DrawHudPanel(rt, ctx);
            DrawDashboardButton(rt);
            DrawUpdateWarning(rt, ctx);
            DrawGuidanceHud(rt, ctx);
        }
        finally
        {
            rt.EndDraw();
        }
        _window.Present();
    }

    /// <summary>
    /// Map-hack overlay rendered ON TOP of PoE's expanded map at exactly PoE's scale and
    /// position — same as the open-source Radar plugin. We compute MapCenter and MapScale
    /// the same way ExileCore does (window-center + DefaultShift + Shift; Zoom × WindowH ÷
    /// 677), so our terrain bitmap and entity dots sit pixel-aligned with PoE's drawing.
    /// </summary>
    private void DrawMapOverlay(ID2D1RenderTarget rt, RenderContext ctx)
    {
        if (ctx.Snapshot is not { } snap) return;
        if (ctx.Live is not { } live) return;          // need fresh player position to project
        var map = snap.Map;
        if (!map.IsLargeMapVisible) return;

        if (map.GetLargeMapCenter(snap.Window) is not { } centerRaw) return;

        // Build/refresh the terrain bitmap if dimensions or area-hash changed.
        _terrainBitmap ??= new TerrainBitmap(rt);
        _terrainBitmap.EnsureBuilt(snap.Nav, snap.AreaHash, IsInTransition(snap));
        if (_terrainBitmap.Bitmap is null)
        {
            DrawLoadingMapIndicator(rt, snap.Window.Width * 0.5f, snap.Window.Height * 0.5f);
            return;
        }

        var bmpW       = _terrainBitmap.Width;
        var bmpH       = _terrainBitmap.Height;
        // Use the LIVE player position (read this frame), not the world snapshot's cached
        // value. World snapshot refreshes at 30 Hz; render at 144 Hz. Projecting cached
        // entity grids through the live player keeps blips smoothly tracking even as the
        // world snapshot ages a few frames.
        var playerGrid = new NumVec2(live.GridPosition.X, live.GridPosition.Y);
        var center     = new NumVec2(centerRaw.X, centerRaw.Y);
        var mapScale   = map.ScaleFor(map.LargeMapZoom, snap.Window.Height);

        // Project bitmap corners through PoE's exact projection. PoE centers on the player —
        // the blip sits at MapCenter and everything else is delta-from-player.
        var p00 = ProjectGrid(new NumVec2(0,    0   ), playerGrid, center, mapScale);
        var p10 = ProjectGrid(new NumVec2(bmpW, 0   ), playerGrid, center, mapScale);
        var p01 = ProjectGrid(new NumVec2(0,    bmpH), playerGrid, center, mapScale);

        var ex = (p10 - p00) / bmpW;
        var ey = (p01 - p00) / bmpH;
        var transform = new System.Numerics.Matrix3x2(ex.X, ex.Y, ey.X, ey.Y, p00.X, p00.Y);

        var prevTransform = rt.Transform;
        rt.Transform = transform;
        rt.DrawBitmap(_terrainBitmap.Bitmap, 1.0f, BitmapInterpolationMode.Linear, new Rect(0, 0, bmpW, bmpH));
        rt.Transform = prevTransform;

        // Player blip projects to MapCenter (delta from self = 0). Off by default — the map is
        // already player-centered so the marker is redundant; opt in via settings.
        if (ctx.PlayerBlip) FillCircle(rt, center, 6f, _bPlayer!);

        // Markers + guidance route use the same player-centered projection
        DrawMapMarkers(rt, ctx, snap, playerGrid, center, mapScale);
        DrawGuidanceOnMap(rt, ctx, playerGrid, center, mapScale);
    }

    /// <summary>
    /// Draw campaign guidance on PoE's expanded map: one gold breadcrumb route per key target
    /// (waypoint, each area transition), plus a labelled cyan diamond at each target. Routes are
    /// precomputed off-thread by the guidance worker.
    /// </summary>
    private void DrawGuidanceOnMap(ID2D1RenderTarget rt, RenderContext ctx, NumVec2 playerGrid, NumVec2 center, float mapScale)
    {
        if (ctx.GuidanceRoutes is not { Count: > 0 } routes) return;

        foreach (var route in routes)
        {
            var brush = BrushForGuidance(route.Kind);
            var cells = route.Cells;
            if (cells.Count >= 2)
            {
                var prev = ProjectGrid(new NumVec2(cells[0].X, cells[0].Y), playerGrid, center, mapScale);
                for (var i = 1; i < cells.Count; i++)
                {
                    var p = ProjectGrid(new NumVec2(cells[i].X, cells[i].Y), playerGrid, center, mapScale);
                    rt.DrawLine(prev, p, brush, 3.0f);
                    prev = p;
                }
            }

            var tp = ProjectGrid(new NumVec2(route.Target.X, route.Target.Y), playerGrid, center, mapScale);
            DrawShape(rt, DotShape.Diamond, tp, 6f, brush, filled: true);
            var rect = new Vortice.RawRectF(tp.X + 8f, tp.Y - 7f, tp.X + 228f, tp.Y + 7f);
            rt.DrawText(route.Label, _tfSmall!, rect, brush, DrawTextOptions.Clip);
        }
    }

    /// <summary>Guidance color by objective kind: waypoint = cyan, area exit = green, boss = red,
    /// everything else (quest objectives etc.) = gold.</summary>
    private ID2D1SolidColorBrush BrushForGuidance(RouteTokenType kind) => kind switch
    {
        RouteTokenType.WaypointGet or RouteTokenType.Waypoint => _bGuideWaypoint!,
        RouteTokenType.Enter or RouteTokenType.Area or RouteTokenType.WaypointUse or RouteTokenType.PortalUse => _bGuideExit!,
        RouteTokenType.Kill or RouteTokenType.Arena => _bGuideBoss!,
        _ => _bGuide!,
    };

    /// <summary>
    /// Draw campaign guidance in the world (ground) view for when PoE's big map is closed — a gold
    /// route line projected onto the ground via the camera matrix, plus a cyan diamond + label at
    /// each objective. Complements <see cref="DrawGuidanceOnMap"/> (which draws on the expanded map).
    /// The route's far cells project off-screen; the on-screen segment still points the way.
    /// </summary>
    private void DrawGuidanceWorld(ID2D1RenderTarget rt, RenderContext ctx)
    {
        if (ctx.GuidanceRoutes is not { Count: > 0 } routes) return;
        if (ctx.Snapshot is not { } snap) return;
        if (snap.Map.IsLargeMapVisible) return;              // on-map version handles the open-map case
        if (snap.Camera is not { IsValid: true } cam) return;
        if (ctx.Live is not { } live) return;
        var pz = live.WorldPosition.Z;

        foreach (var route in routes)
        {
            var brush = BrushForGuidance(route.Kind);
            var cells = route.Cells;
            var prev = cells.Count > 0 ? cam.GridToScreenAtPlayerZ(new BubblesBot.Core.Game.Vector2i { X = cells[0].X, Y = cells[0].Y }, pz) : null;
            for (var i = 1; i < cells.Count; i++)
            {
                var cur = cam.GridToScreenAtPlayerZ(new BubblesBot.Core.Game.Vector2i { X = cells[i].X, Y = cells[i].Y }, pz);
                if (prev is { } a && cur is { } b)
                    rt.DrawLine(new NumVec2(a.X, a.Y), new NumVec2(b.X, b.Y), brush, 3.0f);
                prev = cur;
            }

            if (cam.GridToScreenAtPlayerZ(route.Target, pz) is { } tp)
            {
                var pt = new NumVec2(tp.X, tp.Y);
                DrawShape(rt, DotShape.Diamond, pt, 7f, brush, filled: true);
                var rect = new Vortice.RawRectF(pt.X + 9f, pt.Y - 7f, pt.X + 229f, pt.Y + 7f);
                rt.DrawText(route.Label, _tfSmall!, rect, brush, DrawTextOptions.Clip);
            }
        }
    }

    /// <summary>
    /// Always-on guidance banner (top-center): the next objective + distance, or a diagnostic when
    /// the current area has no route/target coverage. Independent of PoE's big map being open so the
    /// player always knows where to go next.
    /// </summary>
    private void DrawGuidanceHud(ID2D1RenderTarget rt, RenderContext ctx)
    {
        if (ctx.Guidance is not { } g) return;
        if (ctx.Snapshot is not { } snap) return;

        string line;
        if (g.Targets.Count > 0)
            line = $"paths: {string.Join(", ", g.Targets.Select(t => t.Label).Distinct())}";
        else
            line = $"guidance: {g.Diagnostic ?? "no route for this area"}";

        var text = string.IsNullOrEmpty(g.AreaId) ? line : $"[{g.AreaId}] {line}";
        var w = 460f;
        var x = (snap.Window.Width - w) * 0.5f;
        const float y = 12f, h = 24f;
        FillRect(rt, x, y, w, h, _bPanel!);
        DrawRect(rt, x, y, w, h, _bBorder!, 1f);
        Text(rt, text, _tfNormal!, g.Targets.Count > 0 ? _bGuide! : _bDim!, x + 8, y + 4, w - 16, h - 6);
    }

    /// <summary>
    /// Standalone always-on minimap drawn in our overlay corner — independent of PoE's own
    /// minimap state. This is the actual "map hack" surface: even when PoE's minimap is
    /// hidden or the player hasn't expanded it, we always show terrain + entities + path
    /// in a fixed corner panel. Player-centered, isometric projection at our chosen scale.
    /// </summary>
    private void DrawDebugMinimap(ID2D1RenderTarget rt, RenderContext ctx)
    {
        // Only show the corner minimap when PoE's big map (Tab) is open. Drawing it
        // permanently is distracting during normal play; the in-world DrawMapOverlay path
        // already projects onto the game's big-map canvas when expanded, and this corner
        // panel mirrors it for diagnostics. Hide otherwise.
        if (ctx.Snapshot?.Map.IsLargeMapVisible != true) return;

        // ALWAYS draw the panel background — even if we can't draw any contents — so the
        // user can see the panel exists and read the diagnostic message inside it.
        const float panelSize = 360f;
        const float margin    = 16f;
        var winH = ctx.Snapshot?.Window.Height ?? 0;
        var px = margin;
        var py = winH > panelSize + margin + 96 ? winH - panelSize - margin - 96 : margin + 200;

        FillRect(rt, px, py, panelSize, panelSize, _bPanel!);
        DrawRect(rt, px, py, panelSize, panelSize, _bBorder!);
        Text(rt, "minimap", _tfSmall!, _bDim!, px + 6, py + 4, panelSize - 12, 12);

        // Diagnostic short-circuits: tell the user WHY the map isn't drawing.
        if (ctx.Snapshot is not { } snap)
        {
            Text(rt, "no snapshot", _tfNormal!, _bHpLow!, px + 10, py + 28, panelSize - 20, 18);
            return;
        }
        if (ctx.Live is null)
        {
            Text(rt, "no player resolved", _tfNormal!, _bHpLow!, px + 10, py + 28, panelSize - 20, 18);
            return;
        }
        if (!snap.Nav.IsAvailable)
        {
            Text(rt, "no terrain (NavGrid.IsAvailable=false)", _tfNormal!, _bHpLow!, px + 10, py + 28, panelSize - 20, 18);
            Text(rt, "(area not loaded?)", _tfSmall!, _bDim!, px + 10, py + 50, panelSize - 20, 14);
            return;
        }

        var live = ctx.Live.Value;
        var center = new NumVec2(px + panelSize * 0.5f, py + panelSize * 0.5f);
        var playerGrid = new NumVec2(live.GridPosition.X, live.GridPosition.Y);

        // Pick a scale that fits ~the network bubble (180 grid) into the panel. Isometric
        // distance for a (180,180) delta is mapScale × 180 × 2 × cos along X.
        // panelSize / 2 ≈ mapScale × 180 × cos × √2 → mapScale ≈ panelSize / (180×2×cos)
        const float showRadiusGrid = 120f;
        var mapScale = panelSize * 0.5f / (showRadiusGrid * MapProjection.CameraCos * 2f);

        // Clip to the panel rect so projected points outside don't draw on the rest of the screen.
        var clipRect = new Vortice.RawRectF(px, py, px + panelSize, py + panelSize);
        rt.PushAxisAlignedClip(clipRect, AntialiasMode.Aliased);
        try
        {
            // Bake terrain bitmap (shared with the large-map renderer).
            _terrainBitmap ??= new TerrainBitmap(rt);
            _terrainBitmap.EnsureBuilt(snap.Nav, snap.AreaHash, IsInTransition(snap));
            if (_terrainBitmap.Bitmap is not null)
            {
                var bmpW = _terrainBitmap.Width;
                var bmpH = _terrainBitmap.Height;
                var p00 = ProjectGrid(new NumVec2(0,    0   ), playerGrid, center, mapScale);
                var p10 = ProjectGrid(new NumVec2(bmpW, 0   ), playerGrid, center, mapScale);
                var p01 = ProjectGrid(new NumVec2(0,    bmpH), playerGrid, center, mapScale);
                var ex = (p10 - p00) / bmpW;
                var ey = (p01 - p00) / bmpH;
                var transform = new System.Numerics.Matrix3x2(ex.X, ex.Y, ey.X, ey.Y, p00.X, p00.Y);
                var prev = rt.Transform;
                rt.Transform = transform;
                rt.DrawBitmap(_terrainBitmap.Bitmap, 1.0f, BitmapInterpolationMode.Linear, new Rect(0, 0, bmpW, bmpH));
                rt.Transform = prev;
            }
            else
            {
                // Area in transition — show a "loading map" hint at panel center.
                DrawLoadingMapIndicator(rt, center.X, center.Y);
            }

            // Markers
            DrawMapMarkers(rt, ctx, snap, playerGrid, center, mapScale);

            // Player blip on top
            FillCircle(rt, center, 5f, _bPlayer!);
        }
        finally
        {
            rt.PopAxisAlignedClip();
        }

        // Per-cell debug at top: show grid pos + scale so we can sanity-check projection
        Text(rt, $"grid ({(int)playerGrid.X},{(int)playerGrid.Y})  scale {mapScale:F2}",
            _tfSmall!, _bDim!, px + 6, py + panelSize - 14, panelSize - 12, 12);
    }

    /// <summary>
    /// True when the snapshot looks like an area transition is in progress. PoE briefly
    /// leaves the previous area's hash readable AND the player pointer NULL during loading
    /// screens, so hash-equality alone can't tell us the area is changing. Treat any of
    /// (hash=0, player=null, terrain dimensions=0) as "in transition" → drop overlay.
    /// </summary>
    private static bool IsInTransition(BubblesBot.Core.Snapshot.GameSnapshot snap)
    {
        if (snap.AreaHash == 0) return true;
        if (snap.Player is null) return true;
        if (!snap.Nav.IsAvailable) return true;
        return false;
    }

    /// <summary>
    /// Tiny "loading…" placeholder drawn at a screen point while the terrain bitmap is
    /// being rebuilt for a new area. Kept dead simple — no animation, just a label so the
    /// user knows the bot is alive and waiting on memory data.
    /// </summary>
    private void DrawLoadingMapIndicator(ID2D1RenderTarget rt, float x, float y)
    {
        const float w = 160f, h = 28f;
        var bx = x - w * 0.5f;
        var by = y - h * 0.5f;
        FillRect(rt, bx, by, w, h, _bPanel!);
        DrawRect(rt, bx, by, w, h, _bBorder!);
        Text(rt, "loading map…", _tfNormal!, _bDim!, bx, by + 6, w, h - 8);
    }

    /// <summary>
    /// Iterates the pre-flattened dot list (built at world rate by <see cref="EnsureDots"/>)
    /// and draws each via the live projection. The expensive entity walk happens once per
    /// world tick (~30 Hz); rendering N dots per frame is just N projections + N draws,
    /// cheap enough to remain at 144 Hz with 1000+ entities.
    /// </summary>
    private void DrawMapMarkers(ID2D1RenderTarget rt, RenderContext ctx, GameSnapshot snap,
        NumVec2 anchorGrid, NumVec2 center, float mapScale, NumVec2? playerGrid = null)
    {
        EnsureDots(ctx, snap, playerGrid ?? anchorGrid);

        // Pass 1: polygon blob fill (normal+magic union). Single FillGeometry, uniform
        // alpha across the shape — gives clean "merged pack" shapes, no AA fuzz.
        if (_mobBlobGeometry is not null)
        {
            var prev = rt.Transform;
            rt.Transform = MakeGridToScreenMatrix(anchorGrid, center, mapScale);
            rt.FillGeometry(_mobBlobGeometry, _bBlob!);
            rt.DrawGeometry(_mobBlobGeometry, _bBlobOutline!, 1.2f);
            rt.Transform = prev;
        }

        // Pass 2: per-mob bright cores + rare/unique filled circles + ground items. Direct2D
        // batches same-brush primitives, so N small FillEllipse calls go in one GPU dispatch.
        foreach (var d in _entityDots)
        {
            var p = ProjectGrid(new NumVec2(d.Grid.X, d.Grid.Y), anchorGrid, center, mapScale);
            var brush = BrushByIndex(d.BrushIndex);
            DrawShape(rt, d.Shape, p, d.Radius, brush, d.Filled);

            // Label any dot that brought a name (uniques, rares, named mechanics). Anchored
            // to the right of the dot so text doesn't cover it. Centered vertically.
            if (!string.IsNullOrEmpty(d.Label))
            {
                var lx = p.X + d.Radius + 3f;
                var ly = p.Y - 7f;
                var rect = new Vortice.RawRectF(lx, ly, lx + 200f, ly + 14f);
                rt.DrawText(d.Label, _tfSmall!, rect, brush, DrawTextOptions.Clip);
            }
        }
    }

    /// <summary>
    /// Affine matrix that maps grid (X, Y) → screen pixel position, given the current
    /// projection anchor + center + scale. PoE's projection is linear in grid coords (with
    /// our per-pixel-no-height simplification), so it fits in a 3×2 transform.
    /// </summary>
    private static System.Numerics.Matrix3x2 MakeGridToScreenMatrix(NumVec2 anchorGrid, NumVec2 center, float mapScale)
    {
        var p00 = ProjectGrid(new NumVec2(0, 0), anchorGrid, center, mapScale);
        var p10 = ProjectGrid(new NumVec2(1, 0), anchorGrid, center, mapScale);
        var p01 = ProjectGrid(new NumVec2(0, 1), anchorGrid, center, mapScale);
        var ex = p10 - p00;
        var ey = p01 - p00;
        return new System.Numerics.Matrix3x2(ex.X, ex.Y, ey.X, ey.Y, p00.X, p00.Y);
    }

    private ID2D1SolidColorBrush BrushByIndex(int i) => i switch
    {
        0 => _bEnemyWhite!,
        1 => _bEnemyMagic!,
        2 => _bEnemyRare!,
        3 => _bEnemyUnique!,
        4 => _bGround!,
        5 => _bShrine!,
        6 => _bAltar!,
        7 => _bRitual!,
        _ => _bDim!,
    };

    /// <summary>
    /// Rebuild <see cref="_entityDots"/> from the cached <see cref="EntityCache"/> whenever
    /// the snapshot identity changes. The cache already has every entity's frozen fields and
    /// freshly-refreshed mutable fields — we only walk it (in-memory dictionary, no PoE
    /// reads) to filter by bubble + alive + hostile and project to dots.
    /// </summary>
    private void EnsureDots(RenderContext ctx, GameSnapshot snap, NumVec2 playerGrid)
    {
        if (ReferenceEquals(_dotsBuiltForSnapshot, snap)) return;
        _dotsBuiltForSnapshot = snap;
        _entityDots.Clear();

        // Ground items first (small filled yellow). These come from the visible labels list,
        // which is small (<100 typically) so iterating per snapshot is fine.
        foreach (var label in snap.GroundLabels)
        {
            if (!label.IsItem) continue;
            if (!label.IsLabelVisible) continue;
            if (label.EntityGridPosition is not { } g) continue;
            _entityDots.Add(new DotEntry(g, 4, 2.5f, true));
        }

        // Hostile monsters from the cache.
        //   • Rare → yellow filled (above blob)
        //   • Unique → orange filled (above blob)
        //   • Normal + Magic → contribute to the blob geometry; their cores draw as small
        //     bright dots on top via _entityDots.
        var blobPoints = new List<BubblesBot.Core.Game.Vector2i>();
        if (ctx.Entities is { } cache)
        {
            var bubble2 = (float)GridConstants.NetworkBubbleGrid * GridConstants.NetworkBubbleGrid;
            foreach (var entry in cache.Entries.Values)
            {
                if (!entry.IsHostileMonster) continue;
                if (!entry.IsAlive) continue;

                var dx = entry.GridPosition.X - playerGrid.X;
                var dy = entry.GridPosition.Y - playerGrid.Y;
                if (dx * dx + dy * dy > bubble2) continue;

                // Note: we deliberately do NOT filter by a "hidden/dormant" entity flag here.
                // The 0x400 entity-flags bit that looked like IsHidden turned out to track
                // active/animating state, not spawn-state, so it false-flagged idle-but-real mobs.
                // Un-spawned/garbage mobs are handled at runtime by the combat damage-gate
                // (blacklist a target that takes no damage), not by a render/target flag.
                switch (entry.Rarity)
                {
                    case EntityListReader.EntityRarity.Unique:
                        // Skip "uniques" that aren't real bosses: no Life (Volatile Orbs) or
                        // effect-path AoE entities. Ignore-list handles named trash.
                        if (!entry.HasLife) break;
                        if (IsEffectMonsterPath(entry.Metadata)) break;
                        if (EnemyIgnoreList.IsIgnored(entry.Name)) break;
                        _entityDots.Add(new DotEntry(entry.GridPosition, 3, 7.0f, true, DotShape.Star, entry.Name));
                        break;
                    case EntityListReader.EntityRarity.Rare:
                        if (!entry.HasLife) break;
                        if (IsEffectMonsterPath(entry.Metadata)) break;
                        _entityDots.Add(new DotEntry(entry.GridPosition, 2, 6.0f, true, DotShape.Triangle));
                        break;
                    case EntityListReader.EntityRarity.Magic:
                        blobPoints.Add(entry.GridPosition);
                        _entityDots.Add(new DotEntry(entry.GridPosition, 1, 1.6f, true));
                        break;
                    default:
                        blobPoints.Add(entry.GridPosition);
                        _entityDots.Add(new DotEntry(entry.GridPosition, 0, 1.4f, true));
                        break;
                }
            }
        }

        // Map mechanics — shrines, eldritch altars, ritual runes. Distinctive vivid dots
        // so the user (and the bot) can spot them at a glance among monster blobs. Drawn
        // larger than enemy dots and with a name label.
        if (ctx.Entities is { } cache2)
        {
            var mechanics = new BubblesBot.Core.Snapshot.MechanicsView(cache2);
            foreach (var m in mechanics.Entries)
            {
                // Once used, mechanics drop off the minimap entirely — a faint outline kept
                // them on screen indefinitely and added clutter. MechanicsView flips
                // IsActivated via IsTargetable / state-machine reads on use.
                if (m.IsActivated) continue;
                // Consumed Eldritch altars keep Targetable=true in memory, so the entity
                // read never flips — the bot's own ledger is the "used" signal.
                if (m.Kind == BubblesBot.Core.Snapshot.MechanicKind.EldritchAltar
                    && BubblesBot.Bot.Modes.EldritchAltarLedger.IsResolved(snap.AreaHash, m.Id))
                    continue;

                var (idx, shape) = m.Kind switch
                {
                    BubblesBot.Core.Snapshot.MechanicKind.Shrine        => (5, DotShape.Plus),
                    BubblesBot.Core.Snapshot.MechanicKind.EldritchAltar => (6, DotShape.Diamond),
                    BubblesBot.Core.Snapshot.MechanicKind.RitualRune    => (7, DotShape.Square),
                    BubblesBot.Core.Snapshot.MechanicKind.MemoryTear    => (5, DotShape.Star),
                    _ => (-1, DotShape.Circle),
                };
                if (idx < 0) continue;
                var label = string.IsNullOrEmpty(m.Name) ? m.Kind.ToString() : m.Name;
                if (m.IsActive) label += " (active)";
                else if (m.Status == BubblesBot.Core.Snapshot.MechanicStatus.Unknown) label += " (?)";
                _entityDots.Add(new DotEntry(m.GridPosition, idx, 7.0f, true, shape, label));
            }
        }

        BuildMobBlobGeometry(blobPoints);
    }

    /// <summary>
    /// Build a single <see cref="ID2D1PathGeometry"/> with one filled circle per normal/magic
    /// mob, in *grid* coordinates. Rendered with translucent fill via FillGeometry — the
    /// merged shape gets uniform alpha across the whole union, so dense packs read as
    /// defined polygon blobs (not antialias-fuzz from N overlapping discs). Outlining the
    /// same geometry adds a brighter rim that emphasizes the blob shape.
    /// </summary>
    private void BuildMobBlobGeometry(List<BubblesBot.Core.Game.Vector2i> points)
    {
        _mobBlobGeometry?.Dispose();
        _mobBlobGeometry = null;
        if (points.Count == 0) return;

        // Radius in grid units. Larger → blobs merge sooner (fewer holes between mobs);
        // smaller → individual mobs stand out as separate puffs. ~3.5 grid is "loose pack
        // becomes a single shape" at typical mapScale.
        const float radiusGrid = 3.5f;
        const float k          = 0.5522847f; // standard control-point factor for circle-via-Bezier

        var factory = (ID2D1Factory)_window.RenderTarget.Factory;
        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();
        sink.SetFillMode(FillMode.Winding);

        foreach (var p in points)
        {
            var cx = (float)p.X;
            var cy = (float)p.Y;
            sink.BeginFigure(new System.Numerics.Vector2(cx + radiusGrid, cy), FigureBegin.Filled);
            sink.AddBezier(new BezierSegment(
                new System.Numerics.Vector2(cx + radiusGrid,     cy + radiusGrid * k),
                new System.Numerics.Vector2(cx + radiusGrid * k, cy + radiusGrid),
                new System.Numerics.Vector2(cx,                  cy + radiusGrid)));
            sink.AddBezier(new BezierSegment(
                new System.Numerics.Vector2(cx - radiusGrid * k, cy + radiusGrid),
                new System.Numerics.Vector2(cx - radiusGrid,     cy + radiusGrid * k),
                new System.Numerics.Vector2(cx - radiusGrid,     cy)));
            sink.AddBezier(new BezierSegment(
                new System.Numerics.Vector2(cx - radiusGrid,     cy - radiusGrid * k),
                new System.Numerics.Vector2(cx - radiusGrid * k, cy - radiusGrid),
                new System.Numerics.Vector2(cx,                  cy - radiusGrid)));
            sink.AddBezier(new BezierSegment(
                new System.Numerics.Vector2(cx + radiusGrid * k, cy - radiusGrid),
                new System.Numerics.Vector2(cx + radiusGrid,     cy - radiusGrid * k),
                new System.Numerics.Vector2(cx + radiusGrid,     cy)));
            sink.EndFigure(FigureEnd.Closed);
        }
        sink.Close();
        _mobBlobGeometry = geo;
    }


    private static NumVec2 ProjectGrid(NumVec2 cell, NumVec2 playerGrid, NumVec2 center, float mapScale)
    {
        var d = cell - playerGrid;
        var md = MapProjection.GridDeltaToMapDelta(new BubblesBot.Core.Game.Vector2 { X = d.X, Y = d.Y }, mapScale);
        return new NumVec2(center.X + md.X, center.Y + md.Y);
    }

    private void FillCircle(ID2D1RenderTarget rt, NumVec2 center, float radius, ID2D1SolidColorBrush brush)
    {
        var ellipse = new Ellipse(new NumVec2(center.X, center.Y), radius, radius);
        rt.FillEllipse(ellipse, brush);
    }

    private void EnsureShapeGeometries()
    {
        if (_geoTriangle is not null) return;
        var factory = (ID2D1Factory)_window.RenderTarget.Factory;

        _geoTriangle = factory.CreatePathGeometry();
        using (var s = _geoTriangle.Open())
        {
            s.BeginFigure(new NumVec2(0f, -1f), FigureBegin.Filled);
            s.AddLine(new NumVec2( 0.866f, 0.5f));
            s.AddLine(new NumVec2(-0.866f, 0.5f));
            s.EndFigure(FigureEnd.Closed);
            s.Close();
        }

        _geoDiamond = factory.CreatePathGeometry();
        using (var s = _geoDiamond.Open())
        {
            s.BeginFigure(new NumVec2(0f, -1f), FigureBegin.Filled);
            s.AddLine(new NumVec2( 1f, 0f));
            s.AddLine(new NumVec2( 0f, 1f));
            s.AddLine(new NumVec2(-1f, 0f));
            s.EndFigure(FigureEnd.Closed);
            s.Close();
        }

        _geoStar = factory.CreatePathGeometry();
        using (var s = _geoStar.Open())
        {
            const float inner = 0.4f;
            var first = true;
            for (var i = 0; i < 10; i++)
            {
                var angle = -MathF.PI / 2f + i * MathF.PI / 5f;
                var r2 = (i & 1) == 0 ? 1f : inner;
                var pt = new NumVec2(MathF.Cos(angle) * r2, MathF.Sin(angle) * r2);
                if (first) { s.BeginFigure(pt, FigureBegin.Filled); first = false; }
                else s.AddLine(pt);
            }
            s.EndFigure(FigureEnd.Closed);
            s.Close();
        }

        // Plus / medical cross outline. 12-vertex polygon; arms run -1..1, half-width a.
        _geoPlus = factory.CreatePathGeometry();
        using (var s = _geoPlus.Open())
        {
            const float a = 0.35f;
            var pts = new[]
            {
                new NumVec2(-a, -1f), new NumVec2( a, -1f), new NumVec2( a, -a),
                new NumVec2( 1f, -a), new NumVec2( 1f,  a), new NumVec2( a,  a),
                new NumVec2( a,  1f), new NumVec2(-a,  1f), new NumVec2(-a,  a),
                new NumVec2(-1f, a), new NumVec2(-1f, -a), new NumVec2(-a, -a),
            };
            s.BeginFigure(pts[0], FigureBegin.Filled);
            for (var i = 1; i < pts.Length; i++) s.AddLine(pts[i]);
            s.EndFigure(FigureEnd.Closed);
            s.Close();
        }
    }

    /// <summary>
    /// Draw one of the categorical map icons at <paramref name="p"/> with the given
    /// screen-space radius. Circles + squares use D2D primitives directly; triangle / star /
    /// diamond / plus stamp a cached unit geometry via a per-call Scale+Translate transform.
    /// </summary>
    private void DrawShape(ID2D1RenderTarget rt, DotShape shape, NumVec2 p, float r,
        ID2D1SolidColorBrush brush, bool filled)
    {
        if (shape == DotShape.Circle)
        {
            if (filled) FillCircle(rt, p, r, brush);
            else        rt.DrawEllipse(new Ellipse(p, r, r), brush, 1.0f);
            return;
        }
        if (shape == DotShape.Square)
        {
            // 0.85 keeps the square's visual mass roughly matched to a same-r circle.
            var half = r * 0.85f;
            var rect = new Vortice.RawRectF(p.X - half, p.Y - half, p.X + half, p.Y + half);
            if (filled) rt.FillRectangle(rect, brush);
            else        rt.DrawRectangle(rect, brush, 1.5f);
            return;
        }

        EnsureShapeGeometries();
        var geo = shape switch
        {
            DotShape.Triangle => _geoTriangle,
            DotShape.Star     => _geoStar,
            DotShape.Diamond  => _geoDiamond,
            DotShape.Plus     => _geoPlus,
            _ => null,
        };
        if (geo is null) return;

        var prev = rt.Transform;
        // Scale-then-translate: unit shape (radius 1, centered at origin) → screen at p with size r.
        rt.Transform = new System.Numerics.Matrix3x2(r, 0f, 0f, r, p.X, p.Y);
        if (filled) rt.FillGeometry(geo, brush);
        else        rt.DrawGeometry(geo, brush, 1.5f / r);   // compensate for scale
        rt.Transform = prev;
    }

    /// <summary>
    /// Draw a PoE-style nameplate (title text + HP bar) above every visible Unique
    /// monster. The position comes from projecting the entity's <c>BoundsCenter</c>
    /// (Render.Pos + Bounds × 0.5) — that's the visual center of the mob; the nameplate
    /// sits a fixed pixel offset above it. Uses Render.Bounds.Z to push higher for tall
    /// sprites (bosses) so the plate clears the mob model.
    ///
    /// <para>Card layout (per mob): semi-transparent dark background + rarity-colored
    /// border + name text + HP fill bar showing current/max. Nameplate is fixed width
    /// (160 px) regardless of name length to avoid layout jitter as the mob moves.</para>
    /// </summary>
    private void DrawWorldEntityNames(ID2D1RenderTarget rt, RenderContext ctx)
    {
        if (ctx.Snapshot is not { } snap) return;
        if (ctx.Entities is not { } cache) return;
        if (snap.Camera is not { IsValid: true } cam) return;
        if (ctx.Live is not { } live) return;

        var bubble2 = (long)BubblesBot.Core.Pathfinding.GridConstants.NetworkBubbleGrid * BubblesBot.Core.Pathfinding.GridConstants.NetworkBubbleGrid;

        const float CardW = 160f;
        const float CardH = 28f;
        const float HpBarH = 6f;

        foreach (var entry in cache.Entries.Values)
        {
            if (!entry.IsHostileMonster || !entry.IsAlive) continue;
            if (string.IsNullOrEmpty(entry.Name)) continue;
            if (entry.Rarity != EntityListReader.EntityRarity.Unique) continue;
            // Two distinct categories of "fake unique" to filter out:
            //   • No Life component (Volatile Orbs, particle effects)
            //   • Effect-class monster path (Creeping Fire / InvisibleFire / daemon AoEs)
            // We deliberately do NOT filter by IsTargetable alone — ambush mobs are flagged
            // untargetable until the player gets close, and we want them visible on the map.
            if (!entry.HasLife) continue;
            if (IsEffectMonsterPath(entry.Metadata)) continue;
            if (EnemyIgnoreList.IsIgnored(entry.Name)) continue;  // Tormented Spirit, Sister Cassia, …

            var dx = entry.GridPosition.X - live.GridPosition.X;
            var dy = entry.GridPosition.Y - live.GridPosition.Y;
            if ((long)dx * dx + (long)dy * dy > bubble2) continue;

            // Project the visual top-of-bounds — center XY at the very top of the bounding
            // box (Z + bounds.Z). Card sits a constant offset ABOVE this, which keeps it
            // clear of the mob's sprite at any zoom.
            if (!entry.RenderGeometryReadable) continue;
            var rPos = entry.RenderPosition;
            var rBounds = entry.RenderBounds;
            var topWorld = new BubblesBot.Core.Game.Vector3 { X = rPos.X + rBounds.X * 0.5f, Y = rPos.Y + rBounds.Y * 0.5f, Z = rPos.Z + rBounds.Z };
            var screen = cam.WorldToScreen(topWorld);
            if (screen is null) continue;

            var cx = screen.Value.X;
            var cy = screen.Value.Y - 32f;  // sits above the mob's head

            var border = entry.Rarity == EntityListReader.EntityRarity.Unique ? _bEnemyUnique! : _bEnemyRare!;

            // Card background.
            var cardLeft   = cx - CardW * 0.5f;
            var cardTop    = cy - CardH;
            var cardRight  = cx + CardW * 0.5f;
            var cardBottom = cy;
            var bgRect = new Vortice.RawRectF(cardLeft, cardTop, cardRight, cardBottom);
            rt.FillRectangle(bgRect, _bPanel!);
            rt.DrawRectangle(bgRect, border, 1.5f);

            // Name (centered).
            var nameRect = new Vortice.RawRectF(cardLeft + 4, cardTop + 1, cardRight - 4, cardTop + CardH - HpBarH - 1);
            var oldAlign = _tfSmall!.TextAlignment;
            _tfSmall.TextAlignment = TextAlignment.Center;
            rt.DrawText(entry.Name, _tfSmall, nameRect, border, DrawTextOptions.Clip);
            _tfSmall.TextAlignment = oldAlign;

            // HP bar inside the card, hugging the bottom edge.
            if (entry.HpMax > 0)
            {
                var hpFrac = Math.Clamp((float)entry.HpCurrent / entry.HpMax, 0f, 1f);
                var hpInsetX = 3f;
                var barLeft   = cardLeft + hpInsetX;
                var barTop    = cardBottom - HpBarH - 2;
                var barRight  = cardRight - hpInsetX;
                var barBottom = cardBottom - 2;
                var bgBar = new Vortice.RawRectF(barLeft, barTop, barRight, barBottom);
                rt.FillRectangle(bgBar, _bDim!);
                var fillRight = barLeft + (barRight - barLeft) * hpFrac;
                var fillBar = new Vortice.RawRectF(barLeft, barTop, fillRight, barBottom);
                rt.FillRectangle(fillBar, hpFrac < 0.35f ? _bHpLow! : _bHp!);
                rt.DrawRectangle(bgBar, border, 0.8f);
            }
        }
    }

    /// <summary>
    /// Draw a compact HP bar above every alive hostile monster in the bubble, extending the
    /// unique-only nameplate to all rarities. Uniques are skipped here (their nameplate already shows
    /// HP). Bar fill goes green→red below 30%; the border is rarity-colored. All fields read are
    /// cached on the entity cache (refreshed at world rate) so this is projection + draw only — no
    /// memory reads.
    /// </summary>
    private void DrawHpBars(ID2D1RenderTarget rt, RenderContext ctx)
    {
        if (!ctx.HpBars) return;
        if (ctx.Snapshot is not { } snap) return;
        if (ctx.Entities is not { } cache) return;
        if (snap.Camera is not { IsValid: true } cam) return;
        if (ctx.Live is not { } live) return;

        var bubble2 = (long)GridConstants.NetworkBubbleGrid * GridConstants.NetworkBubbleGrid;
        const float BarW = 34f, BarH = 4f;

        foreach (var entry in cache.Entries.Values)
        {
            if (!entry.IsHostileMonster || !entry.IsAlive) continue;
            if (entry.Rarity == EntityListReader.EntityRarity.Unique) continue; // nameplate already shows HP
            if (!entry.HasLife || entry.HpMax <= 0) continue;
            if (IsEffectMonsterPath(entry.Metadata)) continue;

            var dx = entry.GridPosition.X - live.GridPosition.X;
            var dy = entry.GridPosition.Y - live.GridPosition.Y;
            if ((long)dx * dx + (long)dy * dy > bubble2) continue;

            if (!entry.RenderGeometryReadable) continue;
            var rPos = entry.RenderPosition;
            var rBounds = entry.RenderBounds;
            var topWorld = new BubblesBot.Core.Game.Vector3 { X = rPos.X + rBounds.X * 0.5f, Y = rPos.Y + rBounds.Y * 0.5f, Z = rPos.Z + rBounds.Z };
            var screen = cam.WorldToScreen(topWorld);
            if (screen is null) continue;

            var cx = screen.Value.X;
            var top = screen.Value.Y - 35f;   // sit ~15px higher so the bar clears the mob sprite
            var left = cx - BarW * 0.5f;
            var border = entry.Rarity switch
            {
                EntityListReader.EntityRarity.Rare  => _bEnemyRare!,
                EntityListReader.EntityRarity.Magic => _bEnemyMagic!,
                _ => _bEnemyWhite!,
            };

            var frac = Math.Clamp((float)entry.HpCurrent / entry.HpMax, 0f, 1f);
            var bg = new Vortice.RawRectF(left, top, left + BarW, top + BarH);
            rt.FillRectangle(bg, _bBar!);
            var fill = new Vortice.RawRectF(left, top, left + BarW * frac, top + BarH);
            rt.FillRectangle(fill, frac < 0.30f ? _bHpLow! : _bHp!);
            rt.DrawRectangle(bg, border, 0.8f);
        }
    }

    /// <summary>
    /// Paint a chaos-value badge above every visible unique-item ground label. Helps manual
    /// play decide what's worth picking up — same poe.ninja prices the bot's value filter
    /// uses. Unidentified uniques resolve via <see cref="UniqueArtMapping"/>: best price
    /// across all candidate names (e.g. Kaom's Heart vs Replica Kaom's Heart) is shown.
    ///
    /// <para>Performance: one badge per visible label, dictionary lookups only. The label
    /// rect, rarity, and resource-path reads on <see cref="GroundLabelView"/> are lazy +
    /// snapshot-cached, so iterating every frame is cheap.</para>
    /// </summary>
    private void DrawUniqueValueLabels(ID2D1RenderTarget rt, RenderContext ctx)
    {
        if (ctx.Snapshot is not { } snap) return;
        if (ctx.Prices is not { } prices) return;

        foreach (var label in snap.GroundLabels)
        {
            if (!label.IsItem) continue;
            if (label.ItemRarity != EntityListReader.EntityRarity.Unique) continue;
            if (!label.IsLabelVisible) continue;
            if (label.LabelRect is not { } rect) continue;

            // Resolve a price. Identified: direct name lookup. Unidentified: art-mapping
            // resolver → max chaos across candidate names. Voices ÷150 quirk mirrors the
            // value filter so the badge matches what the bot would actually decide.
            float value;
            string label2;
            if (label.IsIdentified)
            {
                var name = ExtractItemName(label.InnerItemPath);
                value = prices.ValueChaos(name);
                if (name.Contains("Voices", StringComparison.OrdinalIgnoreCase)) value /= 150f;
                label2 = FormatChaos(value);
            }
            else
            {
                var candidates = UniqueArtMapping.Shared.Resolve(label.ResourcePath);
                var best = 0f;
                foreach (var c in candidates)
                {
                    var p = prices.ValueChaos(c);
                    if (c.Contains("Voices", StringComparison.OrdinalIgnoreCase)) p /= 150f;
                    if (p > best) best = p;
                }
                value = best;
                // Trailing "?" reminds the player this is best-case across all art candidates.
                label2 = candidates.Count == 0 ? "?" : FormatChaos(best) + "?";
            }

            // Color: orange-yellow accent for high value (>= 50c), dim gray for cheap or
            // unpriced (< 1c). Threshold picked roughly — adjust as the league economy moves.
            var brush = value >= 50f ? _bGround!
                      : value >= 5f  ? _bEnemyUnique!
                      : value >= 1f  ? _bDim!
                      :                _bDim!;

            // Badge sits just above the label rect, centered horizontally. Background pill
            // helps the text read against bright backgrounds (lava floors, blight effects).
            var text = label2;
            const float padX = 4f, padY = 1f, badgeH = 14f;
            // Estimate width: ~7px per char in Consolas 11. Cheap; D2D layout would be more
            // accurate but allocating a TextLayout per label per frame isn't worth it.
            var estW = text.Length * 7f + padX * 2f;
            var bx = rect.X + (rect.Width - estW) * 0.5f;
            var by = rect.Y - badgeH - 2f;
            var bg = new Vortice.RawRectF(bx, by, bx + estW, by + badgeH);
            rt.FillRectangle(bg, _bPanel!);
            var oldAlign = _tfSmall!.TextAlignment;
            _tfSmall.TextAlignment = TextAlignment.Center;
            rt.DrawText(text, _tfSmall, new Vortice.RawRectF(bx, by + padY, bx + estW, by + badgeH), brush, DrawTextOptions.Clip);
            _tfSmall.TextAlignment = oldAlign;
        }
    }

    /// <summary>
    /// True when the monster metadata path identifies a PoE <i>invisible effect/AoE</i>
    /// entity that's flagged as a monster but isn't a fight target — Creeping Fire
    /// (<c>Metadata/Monsters/InvisibleFire/...</c>) and similar effect carriers.
    ///
    /// <para><b>Why path-based, not <c>!IsTargetable</c>:</b> some legitimate ambush
    /// monsters (burrowed enemies, hidden snipers) are marked untargetable until the
    /// player gets close. We want those visible on the map for warning. PoE's "Invisible"
    /// prefix conventionally marks effect entities that are never meant as fight targets.</para>
    ///
    /// <para><b>Why not <c>/Daemon</c>:</b> tempting (the danger-table mods are all
    /// named "FlamespitterDaemon", "ChaosCloudDaemon", etc.) but lots of regular monsters
    /// carry "Daemon" in their metadata path — demonic mobs, Maven daemons, etc. That filter
    /// would hide real fights. Only the <c>Invisible*</c> prefix is reliably an effect.</para>
    /// </summary>
    private static bool IsEffectMonsterPath(string metadata)
    {
        if (string.IsNullOrEmpty(metadata)) return false;
        // InvisibleFire/* — Creeping Fire (Choking Miasma AoE) and its siblings.
        if (metadata.Contains("/InvisibleFire", StringComparison.OrdinalIgnoreCase)) return true;
        // InvisibleMonsters/* — generic effect-carrier entities for league mods.
        if (metadata.Contains("/InvisibleMonsters", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Last path segment of "Metadata/Items/.../Foo" → "Foo".</summary>
    private static string ExtractItemName(string innerPath)
    {
        if (string.IsNullOrEmpty(innerPath)) return string.Empty;
        var slash = innerPath.LastIndexOf('/');
        return slash >= 0 ? innerPath[(slash + 1)..] : innerPath;
    }

    /// <summary>Format a chaos value compactly: <c>0.5c / 12c / 1.2k / 12k</c>.</summary>
    private static string FormatChaos(float value)
    {
        if (value <= 0f)   return "?";
        if (value < 1f)    return $"{value:F1}c";
        if (value < 100f)  return $"{value:F0}c";
        if (value < 1000f) return $"{value:F0}c";
        return $"{value / 1000f:F1}k";
    }

    private void DrawTargetBox(ID2D1RenderTarget rt, RenderContext ctx)
    {
        // Resolve the target against the LIVE snapshot every frame. Holding a stale
        // GroundLabelView reference would freeze the rect to the position it had when the
        // mode last picked it — but the rect changes every tick as the player walks (the
        // label is anchored to a world-space item and projected through the camera). Live
        // resolution by LabelAddress keeps the box tracking the on-screen label correctly.
        var target = ctx.Loot.ResolveCurrentTarget(ctx.Snapshot);
        if (target is null) return;
        var rect = target.LabelRect;
        if (rect is null) return;

        var brush = ctx.Input.IsIdle ? _bTarget! : _bBusy!;
        var r = rect.Value;
        DrawRect(rt, r.X, r.Y, r.Width, r.Height, brush, 2f);

        var dist = target.DistanceToPlayer;
        Text(rt, $"{dist:F0}u", _tfSmall!, brush, r.X, r.Y - 14, r.Width, 14);
    }

    /// <summary>
    /// "What is the bot thinking" panel — arm state plus the active mode's progress and
    /// decision lines (reveal %, hostile census, current target, movement decision). Drawn
    /// mid-left where PoE keeps no fixed UI, so it's readable during normal play.
    /// </summary>
    private void DrawHudPanel(ID2D1RenderTarget rt, RenderContext ctx)
    {
        var stateLine = ctx.Enable?.StateLabel;
        var modeLines = ctx.Hud;
        if (stateLine is null && modeLines is not { Count: > 0 }) return;

        const float x = 12f, y = 260f, lineH = 17f, padX = 8f, padY = 6f, w = 360f;
        var count = (stateLine is null ? 0 : 1) + (modeLines?.Count ?? 0);
        var h = padY * 2 + lineH * count;

        FillRect(rt, x, y, w, h, _bPanel!);
        DrawRect(rt, x, y, w, h, _bBorder!, 1f);

        var row = 0;
        if (stateLine is not null)
            Text(rt, stateLine, _tfNormal!, _bText!, x + padX, y + padY + lineH * row++, w - padX * 2, lineH);
        if (modeLines is not null)
            foreach (var line in modeLines)
                Text(rt, line, _tfNormal!, _bDim!, x + padX, y + padY + lineH * row++, w - padX * 2, lineH);
    }

    /// <summary>Only interactive overlay surface; all pixels outside this button remain
    /// click-through to PoE.</summary>
    private void DrawDashboardButton(ID2D1RenderTarget rt)
    {
        var x = OverlayWindow.DashboardButtonX;
        var y = OverlayWindow.DashboardButtonY;
        var w = OverlayWindow.DashboardButtonWidth;
        var h = OverlayWindow.DashboardButtonHeight;
        var accent = _window.IsDashboardButtonHovered ? _bTarget! : _bGround!;

        FillRect(rt, x, y, w, h, _bPanel!);
        DrawRect(rt, x, y, w, h, accent, _window.IsDashboardButtonHovered ? 2f : 1f);
        Text(rt, "OPEN WEB UI  |  localhost:5666", _tfNormal!, accent,
            x + 10f, y + 6f, w - 20f, h - 8f);
    }

    /// <summary>Small, non-interactive release notice above the normal status panel.</summary>
    private void DrawUpdateWarning(ID2D1RenderTarget rt, RenderContext ctx)
    {
        if (string.IsNullOrWhiteSpace(ctx.UpdateWarning)) return;

        const float x = 12f, y = 232f, w = 360f, h = 22f, padX = 8f;
        FillRect(rt, x, y, w, h, _bPanel!);
        DrawRect(rt, x, y, w, h, _bGround!, 1f);
        Text(rt, ctx.UpdateWarning, _tfSmall!, _bGround!, x + padX, y + 3f, w - padX * 2, h - 4f);
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private void FillRect(ID2D1RenderTarget rt, float x, float y, float w, float h, ID2D1SolidColorBrush brush)
        => rt.FillRectangle(Box(x, y, w, h), brush);

    private void DrawRect(ID2D1RenderTarget rt, float x, float y, float w, float h, ID2D1SolidColorBrush brush, float stroke = 1f)
        => rt.DrawRectangle(Box(x, y, w, h), brush, stroke);

    private static void Text(ID2D1RenderTarget rt, string text, IDWriteTextFormat fmt,
        ID2D1SolidColorBrush brush, float x, float y, float w, float h)
    {
        // DrawTextOptions.Clip clips text glyphs to the layout rect so long strings get
        // truncated at the panel edge instead of overflowing onto the next row.
        rt.DrawText(text, fmt, new Rect(x, y, w, h), brush, DrawTextOptions.Clip);
    }

    private static Vortice.RawRectF Box(float x, float y, float w, float h)
        => new(x, y, x + w, y + h);

    public void Dispose()
    {
        _bHp?.Dispose(); _bHpLow?.Dispose(); _bBar?.Dispose(); _bText?.Dispose(); _bDim?.Dispose();
        _bPanel?.Dispose(); _bBorder?.Dispose(); _bTarget?.Dispose(); _bBusy?.Dispose();
        _bEnemyWhite?.Dispose(); _bEnemyMagic?.Dispose(); _bEnemyRare?.Dispose(); _bEnemyUnique?.Dispose();
        _bGround?.Dispose(); _bPlayer?.Dispose(); _bPath?.Dispose();
        _bBlob?.Dispose(); _bBlobOutline?.Dispose();
        _bShrine?.Dispose(); _bAltar?.Dispose(); _bRitual?.Dispose();
        _bGuide?.Dispose(); _bGuideWaypoint?.Dispose(); _bGuideExit?.Dispose(); _bGuideBoss?.Dispose();
        _mobBlobGeometry?.Dispose();
        _geoTriangle?.Dispose(); _geoStar?.Dispose(); _geoDiamond?.Dispose(); _geoPlus?.Dispose();
        _tfNormal?.Dispose(); _tfSmall?.Dispose(); _tfHeader?.Dispose();
        _terrainBitmap?.Dispose();
    }
}
