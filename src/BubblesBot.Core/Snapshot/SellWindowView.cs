using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Read-side contract for the normal NPC sell window. Item-backed widgets are divided into the
/// vendor-proceeds region and the player's offered-items region using the live semantic labels,
/// never a persisted UIRoot child index.
/// </summary>
public sealed class SellWindowView
{
    public sealed record Item(
        nint Element,
        nint Entity,
        string Metadata,
        string BaseName,
        int StackSize,
        int Width,
        int Height,
        ElementGeometry.Rect? Rect);

    public sealed record Control(
        nint Element,
        string Text,
        ElementGeometry.Rect? Rect);

    private SellWindowView(
        bool isOpen,
        nint panel,
        nint hover,
        IReadOnlyList<Item> allItems,
        IReadOnlyList<Item> vendorOffer,
        IReadOnlyList<Item> playerOffer,
        Control? accept,
        Control? cancel)
    {
        IsOpen = isOpen;
        Panel = panel;
        HoverElement = hover;
        AllItems = allItems;
        VendorOffer = vendorOffer;
        PlayerOffer = playerOffer;
        Accept = accept;
        Cancel = cancel;
    }

    public bool IsOpen { get; }
    public nint Panel { get; }
    public nint HoverElement { get; }
    public IReadOnlyList<Item> AllItems { get; }
    public IReadOnlyList<Item> VendorOffer { get; }
    public IReadOnlyList<Item> PlayerOffer { get; }
    public Control? Accept { get; }
    public Control? Cancel { get; }

    public static SellWindowView Read(MemoryReader reader, nint ingameState)
    {
        reader.TryReadStruct<nint>(ingameState + KnownOffsets.IngameState.UIHover, out var hover);
        nint panel = 0;
        if (!reader.TryReadStruct<nint>(ingameState + KnownOffsets.IngameState.IngameUi, out var ui)
            || ui == 0
            || !reader.TryReadStruct<nint>(ui + KnownOffsets.IngameUiElements.SellWindow, out panel)
            || panel == 0
            || !ElementReader.IsVisibleDeep(reader, panel))
            return new SellWindowView(false, panel, hover, [], [], [], null, null);

        var textNodes = new List<(nint Element, string Text, ElementGeometry.Rect? Rect)>();
        var items = new List<Item>();
        var seenItems = new HashSet<nint>();
        var seen = new HashSet<nint>();
        var queue = new Queue<(nint Address, int Depth)>();
        queue.Enqueue((panel, 0));
        while (queue.Count > 0 && seen.Count < 4_096)
        {
            var (address, depth) = queue.Dequeue();
            if (!seen.Add(address)) continue;
            var element = ElementReader.TryReadSnapshot(reader, address, 256);
            if (element is null) continue;

            if (ElementReader.IsVisibleDeep(reader, address))
            {
                var text = ReadText(reader, address).Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    textNodes.Add((address, text, ElementGeometry.TryReadRect(reader, address)));

                if (reader.TryReadStruct<nint>(
                        address + KnownOffsets.NormalInventoryItem.Item, out var entity)
                    && entity != 0
                    && seenItems.Add(entity)
                    && reader.TryReadStruct<int>(
                        address + KnownOffsets.NormalInventoryItem.Width, out var width)
                    && reader.TryReadStruct<int>(
                        address + KnownOffsets.NormalInventoryItem.Height, out var height)
                    && width is > 0 and <= 24
                    && height is > 0 and <= 24)
                {
                    var metadata = EntityListReader.ReadEntityPath(reader, entity) ?? string.Empty;
                    if (metadata.StartsWith("Metadata/Items/", StringComparison.Ordinal))
                    {
                        var components = EntityComponents.ReadComponentMap(reader, entity);
                        items.Add(new Item(
                            address,
                            entity,
                            metadata,
                            ReadBaseName(reader, components),
                            ReadStackSize(reader, components),
                            width,
                            height,
                            ElementGeometry.TryReadRect(reader, address)));
                    }
                }
            }

            if (depth >= 16) continue;
            foreach (var child in element.Children) queue.Enqueue((child, depth + 1));
        }

        var vendorLabel = FindUniqueText(textNodes, "Nessa's Offer");
        var playerLabel = FindUniqueText(textNodes, "Your Offer");
        var vendor = new List<Item>();
        var player = new List<Item>();
        if (vendorLabel?.Rect is { } vendorRect && playerLabel?.Rect is { } playerRect)
        {
            foreach (var item in items.Where(x => x.Rect is not null))
            {
                var y = item.Rect!.Value.CenterY;
                if (y > vendorRect.CenterY && y < playerRect.CenterY)
                    vendor.Add(item);
                else if (y > playerRect.CenterY)
                    player.Add(item);
            }
        }

        return new SellWindowView(
            true,
            panel,
            hover,
            items,
            vendor,
            player,
            ReadControl(reader, panel, textNodes, "accept"),
            ReadControl(reader, panel, textNodes, "cancel"));
    }

    private static (nint Element, string Text, ElementGeometry.Rect? Rect)? FindUniqueText(
        IReadOnlyList<(nint Element, string Text, ElementGeometry.Rect? Rect)> nodes,
        string text)
    {
        var matches = nodes.Where(x => string.Equals(x.Text, text, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static Control? ReadControl(
        MemoryReader reader,
        nint panel,
        IReadOnlyList<(nint Element, string Text, ElementGeometry.Rect? Rect)> nodes,
        string text)
    {
        var label = FindUniqueText(nodes, text);
        if (label is null) return null;
        var current = label.Value.Element;
        for (var depth = 0; depth < 4; depth++)
        {
            if (!reader.TryReadStruct<nint>(current + KnownOffsets.Element.Parent, out var parent)
                || parent == 0 || parent == current || parent == panel)
                break;
            if (ElementGeometry.TryReadRect(reader, parent) is { Width: > 20, Height: > 15 } rect
                && label.Value.Rect is { } labelRect
                && rect.Width >= labelRect.Width
                && rect.Height >= labelRect.Height
                && rect.Width <= 400
                && rect.Height <= 150)
                return new Control(parent, label.Value.Text, rect);
            current = parent;
        }
        return new Control(label.Value.Element, label.Value.Text, label.Value.Rect);
    }

    private static string ReadText(MemoryReader reader, nint element)
    {
        var text = NativeString.Read(reader, element + KnownOffsets.Element.TextNoTags);
        return string.IsNullOrWhiteSpace(text)
            ? NativeString.Read(reader, element + KnownOffsets.Element.Text)
            : text;
    }

    private static string ReadBaseName(
        MemoryReader reader,
        IReadOnlyDictionary<string, nint> components)
    {
        if (!components.TryGetValue("Base", out var component)
            || !reader.TryReadStruct<nint>(component + KnownOffsets.BaseComponent.ItemInfo, out var info)
            || info == 0)
            return string.Empty;
        return NativeString.Read(reader, info + KnownOffsets.ItemInfo.BaseName);
    }

    private static int ReadStackSize(
        MemoryReader reader,
        IReadOnlyDictionary<string, nint> components)
    {
        if (components.TryGetValue("Stack", out var stack)
            && reader.TryReadStruct<int>(
                stack + KnownOffsets.StackComponent.CurrentCount, out var count)
            && count is > 0 and < 100_000)
            return count;
        return 1;
    }
}
