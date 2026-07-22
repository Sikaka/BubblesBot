using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

public enum DoorBlockageState
{
    Unknown,
    Closed,
    Open,
}

/// <summary>
/// One ground-item label. The cheap fields (<see cref="LabelAddress"/>, <see cref="LabelElementAddress"/>,
/// <see cref="ItemEntityAddress"/>) are populated when the label list is read. Everything else
/// is lazy and cached for the lifetime of the owning <see cref="GameSnapshot"/>.
/// </summary>
public sealed class GroundLabelView
{
    private readonly MemoryReader _reader;
    private readonly PlayerView _player;
    private readonly int _windowWidth;
    private readonly int _windowHeight;

    private bool _pathRead;       private string _path = string.Empty;
    private bool _innerRead;      private nint _innerAddr;        private string _innerPath = string.Empty;
    private bool _entityRead;     private EntityListReader.EntitySnapshot? _entity;
    private bool _rectRead;       private ElementGeometry.Rect? _rect;
    private bool _towerMenuRead;  private IReadOnlyList<ElementGeometry.Rect> _towerMenuButtons = [];

    // ── Inner item attributes ─────────────────────────────────────────
    // The inner Metadata/Items/... entity wraps Mods / Quality / SkillGem / Stack / RenderItem
    // components. We resolve its component map once on first attribute access and cache each
    // attribute on first read. All accessors are safe on missing components (return default).
    private bool _innerCompsRead; private Dictionary<string, nint>? _innerComps;
    private bool _rarityRead;     private EntityListReader.EntityRarity _rarity;
    private bool _identifiedRead; private bool _isIdentified;
    private bool _itemLevelRead;  private int _itemLevel;
    private bool _qualityRead;    private int _quality;
    private bool _gemLevelRead;   private int _gemLevel;
    private bool _stackRead;      private int _stackCount;
    private bool _resourceRead;   private string _resourcePath = string.Empty;
    private bool _displayNameRead; private string _displayName = string.Empty;
    private bool _baseInfoRead;    private string _baseName = string.Empty;
    private int _inventorySlots = 1;
    private bool _corruptedRead;   private bool _isCorrupted;
    private bool _clusterPassiveRead; private int _clusterPassiveCount;

    // ── Outer entity attributes ───────────────────────────────────────
    // For non-item labels (chests, doors) the outer entity is the actionable thing — its
    // Render.Name is what PoE shows in the tooltip ("Chest", "Strongbox", "Smuggler's Cache").
    // Used to denylist plain "Chest" entities so the loot key doesn't waste time on trash.
    private bool _outerCompsRead; private Dictionary<string, nint>? _outerComps;
    private bool _renderNameRead; private string _renderName = string.Empty;
    private bool _doorStateRead; private DoorBlockageState _doorState;

    internal GroundLabelView(
        MemoryReader reader,
        nint labelAddress,
        nint labelElementAddress,
        nint itemEntityAddress,
        PlayerView player,
        int windowWidth,
        int windowHeight)
    {
        _reader = reader;
        LabelAddress = labelAddress;
        LabelElementAddress = labelElementAddress;
        ItemEntityAddress = itemEntityAddress;
        _player = player;
        _windowWidth = windowWidth;
        _windowHeight = windowHeight;
    }

    /// <summary>Address of the LabelOnGround intrusive list node — stable identity across ticks.</summary>
    public nint LabelAddress { get; }
    public nint LabelElementAddress { get; }
    public nint ItemEntityAddress { get; }

    public uint EntityId => Entity?.Id ?? 0;

    /// <summary>
    /// Entity metadata path (e.g. <c>Metadata/Items/Currency/CurrencyPortal</c>). Read once
    /// per label and cached. This is cheap — does NOT trigger the full entity snapshot
    /// (which reads every component). Use <see cref="IsItem"/> for the common filter.
    /// </summary>
    public string Path
    {
        get
        {
            if (_pathRead) return _path;
            _pathRead = true;
            _path = EntityListReader.ReadEntityPath(_reader, ItemEntityAddress) ?? string.Empty;
            return _path;
        }
    }

    /// <summary>
    /// True if this label points at a real ground item (currency, equipment, gem, etc.) —
    /// not an NPC, chest, waypoint, portal, etc. PoE shows labels for many interactable
    /// entities; the looter only wants drops.
    ///
    /// <para>
    /// PoE wraps ground items in a <c>Metadata/MiscellaneousObjects/WorldItem</c> outer
    /// entity. The actual item entity (<c>Metadata/Items/...</c>) is reached via the outer
    /// entity's <c>WorldItem</c> component → <c>ItemPtr</c>. Labels for non-items (chests,
    /// barrels) have no WorldItem component and are filtered out.
    /// </para>
    /// </summary>
    public bool IsItem => InnerItemPath.StartsWith("Metadata/Items/", StringComparison.Ordinal);

    /// <summary>
    /// Path of the underlying item entity (the thing inside the WorldItem wrapper).
    /// Empty string if this label isn't a WorldItem. Cached.
    /// </summary>
    public string InnerItemPath
    {
        get
        {
            EnsureInner();
            return _innerPath;
        }
    }

    /// <summary>Address of the inner item entity, or 0 if this label isn't a WorldItem.</summary>
    public nint InnerItemAddress
    {
        get
        {
            EnsureInner();
            return _innerAddr;
        }
    }

    private void EnsureInner()
    {
        if (_innerRead) return;
        _innerRead = true;

        // Outer must be a WorldItem; if so, find its WorldItem component and read ItemPtr.
        if (!Path.StartsWith("Metadata/MiscellaneousObjects/WorldItem", StringComparison.Ordinal))
            return;

        var components = EntityComponents.ReadComponentMap(_reader, ItemEntityAddress);
        if (!components.TryGetValue("WorldItem", out var compAddr) || compAddr == 0)
            return;

        if (!_reader.TryReadStruct<nint>(compAddr + KnownOffsets.WorldItemComponent.ItemPtr, out var inner) || inner == 0)
            return;

        _innerAddr = inner;
        _innerPath = EntityListReader.ReadEntityPath(_reader, inner) ?? string.Empty;
    }

    public Vector2i? EntityGridPosition => Entity?.GridPosition;

    public float DistanceToPlayer
        => EntityGridPosition is { } g ? _player.DistanceTo(g) : float.PositiveInfinity;

    public ElementGeometry.Rect? LabelRect
    {
        get
        {
            if (_rectRead) return _rect;
            _rectRead = true;
            _rect = ElementGeometry.TryReadRect(_reader, LabelElementAddress);
            return _rect;
        }
    }

    public bool IsRectOnScreen
    {
        get
        {
            var r = LabelRect;
            return r is not null && r.Value.IntersectsWindow(_windowWidth, _windowHeight);
        }
    }

    /// <summary>
    /// Walks the label element's parent chain checking the visible bit (Flags &amp; 0x800).
    /// Matches ExileCore's <c>Element.IsVisible</c>. False when PoE's loot filter has hidden
    /// the label, when the player is too far for the label to render, or when a parent UI
    /// container is collapsed. The map overlay should only show items the player would
    /// actually see in-game — i.e. labels that pass this check.
    /// </summary>
    public bool IsLabelVisible
    {
        get => BubblesBot.Core.Game.ElementReader.IsVisibleLocal(_reader, LabelElementAddress);
    }

    /// <summary>
    /// Visible Blight tower radial-menu buttons in their stable game order. The menu lives at
    /// <c>label.Children[0].Children[3]</c>; an unopened label returns an empty list. Build
    /// order is Chilling, Shock, Empower, Seismic, Minion, Fire. Upgrade menus expose one
    /// button through the same container until the tier-3 branch choice.
    /// </summary>
    public IReadOnlyList<ElementGeometry.Rect> TowerMenuButtons
    {
        get
        {
            if (_towerMenuRead) return _towerMenuButtons;
            _towerMenuRead = true;
            if (!ElementReader.TryGetChild(_reader, LabelElementAddress, 0, out var container)
                || !ElementReader.TryGetChild(_reader, container, 3, out var menu)
                || !ElementReader.IsVisibleDeep(_reader, menu))
                return _towerMenuButtons;

            var snapshot = ElementReader.TryReadSnapshot(_reader, menu, maxChildren: 12);
            if (snapshot is null || snapshot.Children.Count == 0)
                return _towerMenuButtons;

            var buttons = new List<ElementGeometry.Rect>(snapshot.Children.Count);
            foreach (var child in snapshot.Children)
            {
                if (!ElementReader.IsVisibleDeep(_reader, child)) continue;
                if (ElementGeometry.TryReadRect(_reader, child) is { Width: > 1, Height: > 1 } rect)
                    buttons.Add(rect);
            }
            return _towerMenuButtons = buttons;
        }
    }

    /// <summary>
    /// The two-choice Eldritch altar option tree, when this label belongs to an unconsumed
    /// TangleAltar / CleansingFireAltar within render range. Null for every other label and
    /// on any shape mismatch (fail-closed — see <see cref="EldritchAltarChoicesReader"/>).
    /// </summary>
    public EldritchAltarChoiceSet? EldritchAltarChoices
    {
        get
        {
            if (_altarChoicesRead) return _altarChoices;
            _altarChoicesRead = true;
            return _altarChoices = EldritchAltarChoicesReader.TryRead(_reader, LabelElementAddress);
        }
    }
    private bool _altarChoicesRead; private EldritchAltarChoiceSet? _altarChoices;

    private EntityListReader.EntitySnapshot? Entity
    {
        get
        {
            if (_entityRead) return _entity;
            _entityRead = true;
            _entity = EntityListReader.TryReadSnapshot(_reader, ItemEntityAddress);
            return _entity;
        }
    }

    // ── Item attribute getters (lazy, snapshot-scoped cache) ─────────────────
    //
    // Reads are gated through <see cref="InnerComponents"/> which resolves the inner item
    // entity's component name → instance-address map once. Each attribute then reads its
    // own offset on first access. Items missing the relevant component (e.g. a currency
    // stack with no Quality component) return the default value (0 / Normal / "").

    private Dictionary<string, nint>? InnerComponents
    {
        get
        {
            if (_innerCompsRead) return _innerComps;
            _innerCompsRead = true;
            var inner = InnerItemAddress;
            if (inner == 0) return _innerComps = null;
            _innerComps = EntityComponents.ReadComponentMap(_reader, inner);
            return _innerComps;
        }
    }

    /// <summary>Item rarity from the Mods component. <see cref="EntityListReader.EntityRarity.Normal"/> when missing.</summary>
    public EntityListReader.EntityRarity ItemRarity
    {
        get
        {
            if (_rarityRead) return _rarity;
            _rarityRead = true;
            if (InnerComponents is { } c && c.TryGetValue("Mods", out var modsAddr) && modsAddr != 0
                && _reader.TryReadStruct<int>(modsAddr + KnownOffsets.ModsComponent.ItemRarity, out var v) && v >= 0 && v <= 4)
                _rarity = (EntityListReader.EntityRarity)v;
            return _rarity;
        }
    }

    /// <summary>Identified flag. Unidentified uniques need their <see cref="ResourcePath"/> resolved to candidate names.</summary>
    public bool IsIdentified
    {
        get
        {
            if (_identifiedRead) return _isIdentified;
            _identifiedRead = true;
            if (InnerComponents is { } c && c.TryGetValue("Mods", out var modsAddr) && modsAddr != 0
                && _reader.TryReadStruct<byte>(modsAddr + KnownOffsets.ModsComponent.Identified, out var v))
                _isIdentified = v != 0;
            return _isIdentified;
        }
    }

    /// <summary>Item level (Mods +0x248). 0 when the item has no Mods component.</summary>
    public int ItemLevel
    {
        get
        {
            if (_itemLevelRead) return _itemLevel;
            _itemLevelRead = true;
            if (InnerComponents is { } c && c.TryGetValue("Mods", out var modsAddr) && modsAddr != 0)
                _reader.TryReadStruct<int>(modsAddr + KnownOffsets.ModsComponent.ItemLevel, out _itemLevel);
            return _itemLevel;
        }
    }

    /// <summary>Quality percentage (Quality +0x18). 0 when item has no Quality component.</summary>
    public int Quality
    {
        get
        {
            if (_qualityRead) return _quality;
            _qualityRead = true;
            if (InnerComponents is { } c && c.TryGetValue("Quality", out var qAddr) && qAddr != 0)
                _reader.TryReadStruct<int>(qAddr + KnownOffsets.QualityComponent.CurrentQuality, out _quality);
            return _quality;
        }
    }

    /// <summary>Skill-gem level (SkillGem +0x1C). 0 when item isn't a gem.</summary>
    public int GemLevel
    {
        get
        {
            if (_gemLevelRead) return _gemLevel;
            _gemLevelRead = true;
            if (InnerComponents is { } c && c.TryGetValue("SkillGem", out var sgAddr) && sgAddr != 0)
                _reader.TryReadStruct<int>(sgAddr + KnownOffsets.SkillGemComponent.Level, out _gemLevel);
            return _gemLevel;
        }
    }

    /// <summary>Current stack size (Stack +0x18). 0 when item isn't a stackable currency.</summary>
    public int StackCount
    {
        get
        {
            if (_stackRead) return _stackCount;
            _stackRead = true;
            if (InnerComponents is { } c && c.TryGetValue("Stack", out var stAddr) && stAddr != 0)
                _reader.TryReadStruct<int>(stAddr + KnownOffsets.StackComponent.CurrentCount, out _stackCount);
            return _stackCount;
        }
    }

    /// <summary>
    /// Inner item's art / texture resource path (e.g. <c>Art/2DItems/Rings/Ring2Unique.dds</c>).
    /// AutoExile uses this to resolve <em>unidentified</em> uniques to candidate names via a
    /// per-art-path → name-list mapping table (see <c>uniqueArtMapping.json</c>). Empty string
    /// when item has no RenderItem component.
    /// </summary>
    public string ResourcePath
    {
        get
        {
            if (_resourceRead) return _resourcePath;
            _resourceRead = true;
            if (InnerComponents is { } c && c.TryGetValue("RenderItem", out var riAddr) && riAddr != 0)
            {
                if (_reader.TryReadStruct<nint>(riAddr + KnownOffsets.RenderItemComponent.ResourcePath, out var path))
                    _resourcePath = _reader.ReadStringUtf16(path, 260);
            }
            return _resourcePath;
        }
    }

    /// <summary>
    /// Human-readable text on the ground label. TextNoTags is preferred; Text is the
    /// fallback for older label elements. This is the exact gem/unique name shown by PoE,
    /// unlike the trailing metadata-path segment used by the old value filter.
    /// </summary>
    public string DisplayName
    {
        get
        {
            if (_displayNameRead) return _displayName;
            _displayNameRead = true;
            _displayName = NativeString.Read(_reader, LabelElementAddress + KnownOffsets.Element.TextNoTags);
            if (string.IsNullOrWhiteSpace(_displayName))
                _displayName = NativeString.Read(_reader, LabelElementAddress + KnownOffsets.Element.Text);
            _displayName = NormalizeLabelText(_displayName);
            return _displayName;
        }
    }

    /// <summary>Base item name and inventory footprint read through Base.ItemInfo.</summary>
    public string BaseName
    {
        get { EnsureBaseInfo(); return _baseName; }
    }

    /// <summary>Best human item name available without identifying the item.</summary>
    public string ItemName => !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName : BaseName;

    public bool IsCorrupted
    {
        get
        {
            if (_corruptedRead) return _isCorrupted;
            _corruptedRead = true;
            if (InnerComponents is { } c && c.TryGetValue("Base", out var address) && address != 0
                && _reader.TryReadStruct<byte>(address + KnownOffsets.BaseComponent.Corrupted, out var value))
                _isCorrupted = (value & 1) != 0;
            return _isCorrupted;
        }
    }

    /// <summary>
    /// Added-passive count on cluster jewels. The flattened stat id is the current
    /// GameStat.LocalJewelExpansionPassiveNodeCount row (10980). The id is patch-volatile;
    /// unknown/missing data returns 0 and the loot policy fails open.
    /// </summary>
    public int ClusterPassiveCount
    {
        get
        {
            if (_clusterPassiveRead) return _clusterPassiveCount;
            _clusterPassiveRead = true;
            foreach (var (id, value) in ItemStatsReader.Read(_reader, InnerItemAddress))
                if (id == 10980 && value is >= 1 and <= 35)
                    return _clusterPassiveCount = value;
            return 0;
        }
    }

    /// <summary>
    /// Number of slots the item occupies in inventory (width × height). Used by per-slot
    /// chaos-value filters (AutoExile's MinChaosPerSlot). Defaults to 1 — we'd need an
    /// InventoryDimensions / Sockets read to compute the real value; v1 stub returns 1 so
    /// the per-slot threshold collapses to the flat threshold until that's wired in.
    /// </summary>
    public int InventorySlots
    {
        get { EnsureBaseInfo(); return _inventorySlots; }
    }

    private void EnsureBaseInfo()
    {
        if (_baseInfoRead) return;
        _baseInfoRead = true;
        if (InnerComponents is not { } c || !c.TryGetValue("Base", out var address) || address == 0
            || !_reader.TryReadStruct<nint>(address + KnownOffsets.BaseComponent.ItemInfo, out var info)
            || info == 0) return;

        _baseName = NativeString.Read(_reader, info + KnownOffsets.ItemInfo.BaseName);
        if (_reader.TryReadStruct<byte>(info + KnownOffsets.ItemInfo.CellsWidth, out var width)
            && _reader.TryReadStruct<byte>(info + KnownOffsets.ItemInfo.CellsHeight, out var height)
            && width is >= 1 and <= 4 && height is >= 1 and <= 4)
            _inventorySlots = width * height;
    }

    private static string NormalizeLabelText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var lines = text.Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // Item labels can append stack/price information. The first non-empty line is the
        // item name in both the default and custom-filter layouts.
        return lines.Length > 0 ? lines[0] : text.Trim();
    }

    /// <summary>
    /// Outer entity's Render.Name — what PoE shows when you hover the label ("Chest",
    /// "Diviner's Strongbox", "Smuggler's Cache", "Barrel"). Empty for items (the outer is
    /// a generic WorldItem wrapper) and for any entity missing a Render component.
    /// Used by the loot key to denylist plain "Chest" trash containers by default.
    /// </summary>
    public string RenderName
    {
        get
        {
            if (_renderNameRead) return _renderName;
            _renderNameRead = true;
            if (OuterComponents is { } c && c.TryGetValue("Render", out var rc) && rc != 0)
                _renderName = NativeString.Read(_reader, rc + KnownOffsets.RenderComponent.Name);
            return _renderName;
        }
    }

    /// <summary>
    /// Live door state from <c>TriggerableBlockage.IsClosed</c>. Dungeon capture on
    /// 2026-07-22 proved byte 1 while closed and 0 after the verified click; at the same edge
    /// the movement terrain connected through the doorway. Missing components/read failures
    /// remain <see cref="DoorBlockageState.Unknown"/> and must never authorize a click.
    /// </summary>
    public DoorBlockageState DoorState
    {
        get
        {
            if (_doorStateRead) return _doorState;
            _doorStateRead = true;
            if (OuterComponents is not { } components
                || !components.TryGetValue("TriggerableBlockage", out var blockage)
                || blockage == 0
                || !_reader.TryReadStruct<byte>(
                    blockage + KnownOffsets.TriggerableBlockageComponent.IsClosed,
                    out var closed))
                return _doorState = DoorBlockageState.Unknown;
            return _doorState = closed switch
            {
                1 => DoorBlockageState.Closed,
                0 => DoorBlockageState.Open,
                _ => DoorBlockageState.Unknown,
            };
        }
    }

    public bool IsDoorIdentity
        => Path.Contains("/Door", StringComparison.OrdinalIgnoreCase)
           || DisplayName.Equals("Door", StringComparison.OrdinalIgnoreCase)
           || RenderName.Equals("Door", StringComparison.OrdinalIgnoreCase);

    private Dictionary<string, nint>? OuterComponents
    {
        get
        {
            if (_outerCompsRead) return _outerComps;
            _outerCompsRead = true;
            if (ItemEntityAddress != 0)
                _outerComps = EntityComponents.ReadComponentMap(_reader, ItemEntityAddress);
            return _outerComps;
        }
    }
}
