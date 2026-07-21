using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Lazy view of the in-game map UI (large M-key map and corner minimap). Provides everything
/// the overlay renderer needs to project grid coordinates onto the map: visibility flags,
/// the element rect (= map drawing region), pan shift, zoom, and a derived map scale.
///
/// <para>
/// PoE renders the map isometrically with a 38.7° camera tilt; the canonical projection is in
/// <see cref="Pathfinding.MapProjection"/>. This view supplies the parameters; the projection
/// helper does the math.
/// </para>
/// </summary>
public sealed class MapView
{
    private readonly MemoryReader _reader;
    private readonly nint _ingameStateAddress;

    private bool _read;
    private nint _largeMapAddr;
    private nint _smallMinimapAddr;
    private ElementGeometry.Rect? _largeMapRect;
    private ElementGeometry.Rect? _smallMinimapRect;
    private float _largeZoom;
    private float _smallZoom;
    private Vector2 _largeShift;
    private Vector2 _largeDefaultShift;
    private Vector2 _smallShift;
    private Vector2 _smallDefaultShift;
    private bool _largeVisible;
    private bool _smallVisible;

    internal MapView(MemoryReader reader, nint ingameStateAddress)
    {
        _reader = reader;
        _ingameStateAddress = ingameStateAddress;
    }

    public bool IsLargeMapVisible { get { Ensure(); return _largeVisible; } }
    public bool IsMinimapVisible  { get { Ensure(); return _smallVisible; } }

    /// <summary>Address of the LargeMap element pointer (for debug/diagnostics).</summary>
    public nint LargeMapAddress { get { Ensure(); return _largeMapAddr; } }
    public nint SmallMinimapAddress { get { Ensure(); return _smallMinimapAddr; } }

    /// <summary>Raw Element.Flags value of the LargeMap. Bit 0x04 = IsVisibleLocal.</summary>
    public uint LargeMapFlagsRaw
    {
        get
        {
            Ensure();
            if (_largeMapAddr == 0) return 0;
            return _reader.TryReadStruct<uint>(_largeMapAddr + KnownOffsets.Element.Flags, out var f) ? f : 0;
        }
    }

    /// <summary>Window-relative rect of the large map element. Null if the panel isn't loaded.</summary>
    public ElementGeometry.Rect? LargeMapRect { get { Ensure(); return _largeMapRect; } }
    public ElementGeometry.Rect? MinimapRect  { get { Ensure(); return _smallMinimapRect; } }

    /// <summary>User-controlled zoom level on the large map (scroll-wheel adjusted).</summary>
    public float LargeMapZoom { get { Ensure(); return _largeZoom; } }
    public float MinimapZoom  { get { Ensure(); return _smallZoom; } }

    /// <summary>User pan offset on the large map (Vector2 in screen pixels). Default 0,0.</summary>
    public Vector2 LargeMapShift { get { Ensure(); return _largeShift; } }
    public Vector2 LargeMapDefaultShift { get { Ensure(); return _largeDefaultShift; } }
    public Vector2 MinimapShift  { get { Ensure(); return _smallShift; } }
    public Vector2 MinimapDefaultShift { get { Ensure(); return _smallDefaultShift; } }

    /// <summary>
    /// Derived projection scale matching ExileCore's <c>SubMap.MapScale</c>:
    /// <c>Zoom × WindowHeight / 677.0</c>. The 677.0 divisor was derived from a live POEMCP
    /// probe at 1920×1080/Zoom=0.5 producing MapScale=0.7976366; the formula is
    /// resolution-independent (PoE scales by window height).
    /// </summary>
    public float ScaleFor(float zoom, int windowHeight)
        => windowHeight > 0 ? zoom * windowHeight / KnownOffsets.SubMap.MapScaleHeightDivisor : zoom;

    /// <summary>
    /// Returns the on-screen center the large map is drawn around. Matches ExileCore's
    /// <c>SubMap.MapCenter</c>: window center + DefaultShift (PoE's built-in offset, e.g.
    /// (0, -20) to push the map below the area-name banner) + Shift (user pan via drag).
    /// Validated 2026-05-05 against POEMCP: WindowCenter (960,540) + DefaultShift (0,-20) =
    /// (960, 520) matches reported MapCenter (959.85, 520).
    /// </summary>
    public Vector2? GetLargeMapCenter(WindowInfo window)
    {
        Ensure();
        if (!_largeVisible) return null;
        if (!window.IsValid) return null;
        return new Vector2
        {
            X = window.Width  * 0.5f + _largeDefaultShift.X + _largeShift.X,
            Y = window.Height * 0.5f + _largeDefaultShift.Y + _largeShift.Y,
        };
    }

    public Vector2? MinimapCenter => MinimapRect is { } r
        ? new Vector2 { X = r.X + r.Width * 0.5f + MinimapShift.X, Y = r.Y + r.Height * 0.5f + MinimapShift.Y }
        : null;

    private void Ensure()
    {
        if (_read) return;
        _read = true;

        if (!_reader.TryReadStruct<nint>(_ingameStateAddress + KnownOffsets.IngameState.IngameUi, out var ui) || ui == 0) return;
        if (!_reader.TryReadStruct<nint>(ui + KnownOffsets.IngameUiElements.Map, out var map) || map == 0) return;

        _reader.TryReadStruct(map + KnownOffsets.MapPanel.LargeMap,     out _largeMapAddr);
        _reader.TryReadStruct(map + KnownOffsets.MapPanel.SmallMiniMap, out _smallMinimapAddr);

        if (_largeMapAddr != 0)
        {
            _largeVisible      = ReadVisible(_largeMapAddr);
            _largeMapRect      = ElementGeometry.TryReadRect(_reader, _largeMapAddr);
            _largeZoom         = ReadFloat(_largeMapAddr + KnownOffsets.SubMap.Zoom);
            _largeShift        = ReadVec2 (_largeMapAddr + KnownOffsets.SubMap.Shift);
            _largeDefaultShift = ReadVec2 (_largeMapAddr + KnownOffsets.SubMap.DefaultShift);
        }
        if (_smallMinimapAddr != 0)
        {
            _smallVisible      = ReadVisible(_smallMinimapAddr);
            _smallMinimapRect  = ElementGeometry.TryReadRect(_reader, _smallMinimapAddr);
            _smallZoom         = ReadFloat(_smallMinimapAddr + KnownOffsets.SubMap.Zoom);
            _smallShift        = ReadVec2 (_smallMinimapAddr + KnownOffsets.SubMap.Shift);
            _smallDefaultShift = ReadVec2 (_smallMinimapAddr + KnownOffsets.SubMap.DefaultShift);
        }
    }

    /// <summary>
    /// Walk the parent chain checking IsVisibleLocal at every level — matches ExileCore's
    /// <c>Element.IsVisible</c> semantics.
    /// IsVisibleLocal is bit 11 (mask 0x800), NOT bit 2 (0x04). Old ExileApi used 0x04;
    /// modern ExileCore changed the bit position. Validated 2026-05-05 against POEMCP:
    /// LargeMap (visible) had Flags 0x1402EF1 (bit 11 = 1); SmallMM (hidden) had 0x14026F5
    /// (bit 11 = 0). All parent elements in the chain also had bit 11 set when visible.
    /// </summary>
    private bool ReadVisible(nint elementAddr)
    {
        return BubblesBot.Core.Game.ElementReader.IsVisibleDeep(_reader, elementAddr);
    }

    private float ReadFloat(nint addr)
        => _reader.TryReadStruct<float>(addr, out var v) ? v : 0f;

    private Vector2 ReadVec2(nint addr)
        => _reader.TryReadStruct<Vector2>(addr, out var v) ? v : default;
}
