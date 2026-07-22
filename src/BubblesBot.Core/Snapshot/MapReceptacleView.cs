using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Legacy standalone Map Receptacle used by Kirac invitations. This is distinct from the modern
/// Atlas panel/map device and is exposed directly at <c>IngameUi + 0x7C0</c>.
/// </summary>
public sealed class MapReceptacleView
{
    public readonly record struct StagedItem(
        nint Element,
        nint ItemEntity,
        ElementGeometry.Rect? Rect,
        string Path,
        string BaseName,
        EntityListReader.EntityRarity Rarity,
        IReadOnlyList<(int Id, int Value)> Stats);

    private readonly MemoryReader _reader;

    private MapReceptacleView(MemoryReader reader, nint panel, bool isVisible)
    {
        _reader = reader;
        Panel = panel;
        IsVisible = isVisible;
    }

    public nint Panel { get; }
    public bool IsVisible { get; }

    public static MapReceptacleView FromIngameUi(MemoryReader reader, nint ingameState)
    {
        if (!reader.TryReadStruct<nint>(ingameState + KnownOffsets.IngameState.IngameUi, out var ingameUi)
            || ingameUi == 0
            || !reader.TryReadStruct<nint>(
                ingameUi + KnownOffsets.IngameUiElements.MapReceptacleWindow, out var panel)
            || panel == 0)
            return new MapReceptacleView(reader, 0, false);
        return new MapReceptacleView(reader, panel, ElementReader.IsVisibleDeep(reader, panel));
    }

    /// <summary>The currently staged invitation/map, or null when the receptacle is empty.</summary>
    public StagedItem? Item()
    {
        if (!IsVisible) return null;
        var slot = ChildAddress(Panel, 8);
        var element = ChildAddress(slot, 1);
        if (element == 0
            || !_reader.TryReadStruct<nint>(
                element + KnownOffsets.NormalInventoryItem.Item, out var entity)
            || entity == 0)
            return null;
        var path = EntityListReader.ReadEntityPath(_reader, entity) ?? string.Empty;
        var (baseName, rarity, _) = InventoryView.ReadItemIdentity(_reader, entity);
        return new StagedItem(
            element,
            entity,
            ElementGeometry.TryReadRect(_reader, element),
            path,
            baseName,
            rarity,
            ItemStatsReader.Read(_reader, entity));
    }

    public ElementGeometry.Rect? ActivateButtonRect()
    {
        if (!IsVisible) return null;
        var button = ChildAddress(Panel, 5);
        return button == 0 ? null : ElementGeometry.TryReadRect(_reader, button);
    }

    private nint ChildAddress(nint parent, int index)
    {
        if (parent == 0
            || !_reader.TryReadStruct<nint>(parent + KnownOffsets.Element.Childs, out var begin)
            || begin == 0
            || !_reader.TryReadStruct<nint>(begin + index * sizeof(long), out var child))
            return 0;
        return child;
    }
}
