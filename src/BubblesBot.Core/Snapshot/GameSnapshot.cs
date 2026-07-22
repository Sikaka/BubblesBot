using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// One tick's view of the game. Construct it once per tick from the bot loop, hand it to
/// modes/systems, then discard.
///
/// Every accessor is lazy — components/UI/labels are only read on first touch and cached
/// for the lifetime of this snapshot. A mode that doesn't ask for ground labels never pays
/// to read them. This is what keeps the loop scaling as feature surface grows.
///
/// Cross-tick caches (frozen entity fields like Path, BaseType) are owned by the read-side
/// helpers, not by this snapshot.
/// </summary>
public sealed class GameSnapshot
{
    private readonly MemoryReader _reader;
    private readonly nint _ingameDataAddress;
    private readonly nint _ingameStateAddress;
    private readonly WindowInfo _window;

    private PlayerView? _player;
    private IReadOnlyList<GroundLabelView>? _labels;
    private NavGrid? _nav;
    private MapView? _map;
    private CameraView? _camera;
    private SkillBarView? _skillBar;
    private LiveSkillsView? _liveSkills;
    private uint? _areaHash;

    public GameSnapshot(
        MemoryReader reader,
        nint ingameDataAddress,
        nint ingameStateAddress,
        WindowInfo window)
    {
        _reader = reader;
        _ingameDataAddress = ingameDataAddress;
        _ingameStateAddress = ingameStateAddress;
        _window = window;
    }

    public WindowInfo Window => _window;
    public DateTime ReadAt { get; } = DateTime.UtcNow;

    /// <summary>
    /// Underlying memory reader. Exposed so render-side code can do per-frame entity reads
    /// (e.g. plotting hostile monsters on the map overlay) without needing the snapshot to
    /// pre-build a list.
    /// </summary>
    public MemoryReader Reader => _reader;

    /// <summary>Address of the IngameData root — for reads not modeled as snapshot views.</summary>
    public nint IngameDataAddress => _ingameDataAddress;

    /// <summary>Address of the current IngameState root for specialized UI views.</summary>
    public nint IngameStateAddress => _ingameStateAddress;

    /// <summary>
    /// Per-area instance hash. Changes on every area transition (new map, new instance of
    /// the same map, hideout ↔ map, etc). Use this to invalidate per-area caches like the
    /// terrain bitmap.
    /// </summary>
    public uint AreaHash
    {
        get
        {
            if (_areaHash is { } cached) return cached;
            _reader.TryReadStruct<uint>(_ingameDataAddress + Game.KnownOffsets.IngameData.CurrentAreaHash, out var hash);
            _areaHash = hash;
            return hash;
        }
    }

    /// <summary>
    /// Static-terrain navigation view. Lazy: cell reads only happen when something asks
    /// for them. Same NavGrid instance for the snapshot's lifetime so cached cell reads
    /// are reused across multiple queries within one tick.
    /// </summary>
    public NavGrid Nav => _nav ??= new NavGrid(_reader, _ingameDataAddress);

    /// <summary>
    /// In-game map UI state — large M-key map and corner minimap. Lazy: only reads when
    /// touched. Use this to know when to draw a terrain overlay and how to project grid
    /// coordinates onto the map.
    /// </summary>
    public MapView Map => _map ??= new MapView(_reader, _ingameStateAddress);

    /// <summary>
    /// Camera projection — world (or grid) coordinates → screen. One read per snapshot;
    /// matrix is stable for the snapshot's lifetime.
    /// </summary>
    public CameraView Camera => _camera ??= new CameraView(_reader, _ingameStateAddress);

    /// <summary>
    /// Static-per-area tile landmark grid — names + positions for every terrain tile in the
    /// area. Cached across snapshots by area hash, so first call in a new area pays the
    /// ~20 ms scan cost; subsequent calls are dictionary lookups.
    /// </summary>
    public TileMapView TileMap => TileMapView.GetForArea(_reader, _ingameDataAddress, AreaHash);

    /// <summary>
    /// Static tile-entity markers (camera zooms, area transitions, league mechanic hooks).
    /// Walks the same TgtArray as <see cref="TileMap"/> but pulls each tile's per-tile entity
    /// list rather than its detail name. Use for "where will the Ultimatum spawn" → grid
    /// position lookups before the actual interactable streams in.
    /// </summary>
    public TileEntitiesView TileEntities => TileEntitiesView.GetForArea(_reader, _ingameDataAddress, AreaHash);

    /// <summary>
    /// 13-slot skill bar contents. Built off ServerData; gem IDs (UInt16) only — slot indices
    /// are the stable handle, not gem names.
    /// </summary>
    /// <summary>
    /// Map device interaction window — null when the device isn't open. Resolved via the
    /// committed <see cref="UiIndexPaths.MapDeviceWindow"/> path; <see cref="MapDeviceView.IsVisible"/>
    /// tells you whether the panel is actually showing right now.
    /// </summary>
    public MapDeviceView MapDevice
    {
        get
        {
            if (!_reader.TryReadStruct<nint>(_ingameStateAddress + KnownOffsets.IngameState.UIRoot, out var uiRoot) || uiRoot == 0)
                return new MapDeviceView(_reader, 0);
            return MapDeviceView.FromUiTree(_reader, uiRoot);
        }
    }

    /// <summary>
    /// Atlas panel — the modern combined atlas + map device + storage UI. Visible whenever
    /// the player has the atlas open (M key) or has interacted with the map device. See
    /// <see cref="AtlasPanelView"/> for stored maps / device slots / activate button reads.
    /// </summary>
    public AtlasPanelView AtlasPanel => AtlasPanelView.FromIngameUi(_reader, _ingameStateAddress);

    /// <summary>Standalone Kirac invitation/map receptacle, distinct from <see cref="AtlasPanel"/>.</summary>
    public MapReceptacleView MapReceptacle
        => MapReceptacleView.FromIngameUi(_reader, _ingameStateAddress);

    /// <summary>
    /// Ultimatum between-wave / mod-selection panel. <see cref="UltimatumPanelView.IsVisible"/>
    /// is the gate the mode polls to know when a round has completed and choices are up.
    /// </summary>
    public UltimatumPanelView UltimatumPanel => UltimatumPanelView.FromIngameUi(_reader, _ingameStateAddress);

    /// <summary>Ritual Favours shop, tribute/rerolls, and typed item offers.</summary>
    public RitualWindowView RitualWindow => RitualWindowView.FromIngameUi(_reader, _ingameStateAddress);

    /// <summary>Global Ritual Favours button, usable from anywhere in the map.</summary>
    public RitualRewardsButtonView RitualRewardsButton
        => RitualRewardsButtonView.FromIngameUi(_reader, _ingameStateAddress);

    /// <summary>
    /// "Return to area" boundary-warning panel for the Ultimatum encounter. Used by the
    /// mode for real-time out-of-bounds detection and dynamic radius calibration.
    /// </summary>
    public UltimatumBoundaryView UltimatumBoundary => UltimatumBoundaryView.FromIngameUi(_reader, _ingameStateAddress);

    /// <summary>
    /// Ultimatum encounter entities (spawner + capture runes) filtered out of the entity
    /// cache. Pass the cache in via <see cref="EntityCache"/>; this is a thin filter, no
    /// extra memory reads.
    /// </summary>
    public UltimatumView Ultimatum(EntityCache cache) => new(cache, _reader);

    /// <summary>Live blight encounter countdown ("M:SS"), null when no encounter UI is up.
    /// "0:00"/"00:00" indicates the timer has elapsed (no new spawns coming).</summary>
    public string? BlightCountdown => BlightTimerView.ReadCountdownText(_reader, _ingameStateAddress);

    /// <summary>True when the blight countdown UI shows 0:00 — encounter timer expired.</summary>
    public bool IsBlightTimerDone => BlightTimerView.IsTimerDone(BlightCountdown);

    /// <summary>
    /// The blight encounter's "skip" (fast-forward) button. <see cref="BlightSkipButtonView.IsVisible"/>
    /// is the gate — only true while the encounter UI is up AND the skip button is interactable.
    /// Click <see cref="BlightSkipButtonView.ClickRect"/> to end the pre-wave wait period.
    /// </summary>
    public BlightSkipButtonView BlightSkipButton => BlightSkipButtonView.From(_reader, _ingameStateAddress);

    /// <summary>Active mirror encounter's manual end button; ambiguous shared-button states fail closed.</summary>
    public DeliriumEndButtonView DeliriumEndButton
        => DeliriumEndButtonView.FromIngameUi(_reader, _ingameStateAddress);

    /// <summary>Current tower-building currency from the visible Blight encounter HUD.</summary>
    public BlightCurrencyView BlightCurrency => BlightCurrencyView.FromIngameUi(_reader, _ingameStateAddress);

    /// <summary>
    /// Player inventory items (rects + paths + stack sizes). Populated only while the
    /// inventory panel is open; <see cref="InventoryView.IsOpen"/> reports that. Drives the
    /// stacked-deck deposit step (per-item Ctrl+click rects) and Portal-Scroll counting.
    /// </summary>
    public InventoryView Inventory => InventoryView.FromIngameUi(_reader, _ingameStateAddress);

    /// <summary>Visible items in the currently selected stash tab.</summary>
    public StashInventoryView StashInventory
        => StashInventoryView.FromIngameUi(_reader, _ingameStateAddress);

    /// <summary>Current cursor action state (free, holding item, using item, or holding for sale).</summary>
    public CursorView Cursor => CursorView.FromIngameUi(_reader, _ingameStateAddress);

    /// <summary>Server stash-tab names/types/display order for verified tab routing.</summary>
    public StashTabsView StashTabs => StashTabsView.FromIngameData(_reader, _ingameDataAddress);

    /// <summary>Exact visible stash-tab label controls, scoped to the stash tab strip.</summary>
    public StashTabStripView StashTabStrip
        => StashTabStripView.FromIngameUi(_reader, _ingameStateAddress);

    /// <summary>Numbered subinventory controls in the specialized Maps stash tab.</summary>
    public MapStashNavigationView MapStashNavigation
        => MapStashNavigationView.FromIngameUi(_reader, _ingameStateAddress);

    /// <summary>
    /// Unified open-window sweep over the whole IngameUi panel table — confirming intended
    /// opens, spotting accidental ones (<see cref="OpenPanelsView.OpenExcept"/>), and
    /// verifying closes. Built fresh per access; cache the view locally when reading
    /// multiple panels in one decision.
    /// </summary>
    public OpenPanelsView OpenPanels => OpenPanelsView.FromIngameUi(_reader, _ingameStateAddress);

    /// <summary>The "you have died" resurrect panel. <see cref="ResurrectPanelView.IsVisible"/>
    /// is the death signal; the view locates the checkpoint/town buttons by label.</summary>
    public ResurrectPanelView ResurrectPanel => ResurrectPanelView.FromIngameUi(_reader, _ingameStateAddress);

    /// <summary>
    /// True when the stash panel is open. Read via IngameUi → StashElement; non-null pointer
    /// with the element's visible flag set. The deposit step gates Ctrl+click on this — items
    /// only route into the stash while it's actually showing.
    /// </summary>
    public bool IsStashOpen
    {
        get
        {
            if (!_reader.TryReadStruct<nint>(_ingameStateAddress + KnownOffsets.IngameState.IngameUi, out var ingameUi)
                || ingameUi == 0) return false;
            if (!_reader.TryReadStruct<nint>(ingameUi + KnownOffsets.IngameUiElements.StashElement, out var stash)
                || stash == 0) return false;
            const uint visibleBit = 0x800;
            return _reader.TryReadStruct<uint>(stash + KnownOffsets.Element.Flags, out var flags)
                && (flags & visibleBit) != 0;
        }
    }

    /// <summary>
    /// True when PoE has a left-docked panel open — the World Map shares this slot. After
    /// clicking a waypoint, this flips to true once the destination chooser appears. Reading
    /// IngameUi → OpenLeftPanel (+0x5E0); non-null pointer is the open signal.
    /// </summary>
    public bool IsLeftPanelOpen
    {
        get
        {
            if (!_reader.TryReadStruct<nint>(_ingameStateAddress + KnownOffsets.IngameState.IngameUi, out var ingameUi)
                || ingameUi == 0) return false;
            if (!_reader.TryReadStruct<nint>(ingameUi + KnownOffsets.IngameUiElements.OpenLeftPanel, out var panel))
                return false;
            return panel != 0;
        }
    }

    public SkillBarView SkillBar
    {
        get
        {
            if (_skillBar is not null) return _skillBar;
            _reader.TryReadStruct<nint>(_ingameDataAddress + KnownOffsets.IngameData.ServerData, out var serverData);
            return _skillBar = new SkillBarView(_reader, serverData);
        }
    }

    private string? _league;
    /// <summary>
    /// Current league name from ServerData ("Standard", "Mirage", …). The offset is
    /// canary-validated at startup. Empty while ServerData isn't readable (loading screen).
    /// Drives poe.ninja pricing — league-scoped markets value items very differently.
    /// </summary>
    public string League
    {
        get
        {
            if (_league is not null) return _league;
            _reader.TryReadStruct<nint>(_ingameDataAddress + KnownOffsets.IngameData.ServerData, out var serverData);
            return _league = serverData != 0
                ? NativeString.Read(_reader, serverData + KnownOffsets.ServerData.League, maxChars: 64)
                : string.Empty;
        }
    }

    /// <summary>
    /// Live readout of the player's bound skills with current cooldown state. Drives the
    /// dashboard's "Detected skills" panel for one-click profile import.
    /// </summary>
    public LiveSkillsView LiveSkills
    {
        get
        {
            if (_liveSkills is not null) return _liveSkills;
            _reader.TryReadStruct<nint>(_ingameDataAddress + KnownOffsets.IngameData.ServerData, out var serverData);
            var actor = Player?.ActorComponentAddress ?? 0;
            return _liveSkills = new LiveSkillsView(this, serverData, actor);
        }
    }

    /// <summary>Lazy player view. Returns null if the local player pointer is missing.</summary>
    public PlayerView? Player
    {
        get
        {
            if (_player is not null) return _player;
            if (!_reader.TryReadStruct<nint>(_ingameDataAddress + KnownOffsets.IngameData.LocalPlayer, out var addr)
                || addr == 0)
                return null;
            _player = new PlayerView(_reader, addr);
            return _player;
        }
    }

    /// <summary>
    /// Visible ground item labels. Only the cheap header fields are read up front; per-label
    /// heavy reads (rect, entity components) happen lazily as code touches them.
    /// </summary>
    public IReadOnlyList<GroundLabelView> GroundLabels
    {
        get
        {
            if (_labels is not null) return _labels;
            var player = Player;
            if (player is null)
                return _labels = Array.Empty<GroundLabelView>();

            var rootPtr = ReadUiPanelPointer(KnownOffsets.IngameUiElements.ItemsOnGroundLabelRoot);
            if (rootPtr == 0)
                return _labels = Array.Empty<GroundLabelView>();

            var raw = GroundLabelReader.ReadLabels(_reader, rootPtr);
            var views = new List<GroundLabelView>(raw.Count);
            foreach (var l in raw)
                views.Add(new GroundLabelView(
                    _reader, l.Address, l.LabelElement, l.ItemEntity,
                    player, _window.Width, _window.Height));

            return _labels = views;
        }
    }

    private nint ReadUiPanelPointer(int offsetFromIngameUi)
    {
        // IngameState → IngameUi (root element). Panel pointers live at offsets within IngameUi.
        if (!_reader.TryReadStruct<nint>(_ingameStateAddress + KnownOffsets.IngameState.IngameUi, out var ingameUi)
            || ingameUi == 0)
            return 0;
        return _reader.TryReadStruct<nint>(ingameUi + offsetFromIngameUi, out var ptr) ? ptr : 0;
    }
}
