using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Read-side contract for a normal NPC purchase window. It enumerates offers and reads each
/// offer's rendered tooltip tree; it never sends input or infers identity from coordinates.
/// </summary>
public sealed class VendorPurchaseView
{
    public sealed record Requirements(int Strength, int Dexterity, int Intelligence);
    public sealed record CostEntry(
        int Count,
        string Currency,
        ColorBGRA CountTextColor,
        ColorBGRA CurrencyTextColor);

    public sealed record Offer(
        nint Element,
        nint Item,
        nint RenderedTooltip,
        string TreePath,
        string Metadata,
        string BaseName,
        ElementGeometry.Rect? Rect,
        bool IsVisible,
        uint ElementFlags,
        IReadOnlyList<string> TooltipLines,
        IReadOnlyList<CostEntry> Costs,
        Requirements RequiredAttributes)
    {
        public string TooltipText => string.Join('\n', TooltipLines);
        public string GeneratedName => TooltipLines.FirstOrDefault(x =>
            x.Contains(BaseName, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;

        public int? CostCount => Costs.Count == 1 ? Costs[0].Count : null;
        public string CostCurrency => Costs.Count == 1 ? Costs[0].Currency : string.Empty;
    }

    public sealed record PageControl(
        int Page,
        nint Element,
        ElementGeometry.Rect? Rect,
        string Text,
        ColorBGRA BackgroundColor,
        ColorBGRA TextColor,
        ColorBGRA BorderColor,
        byte HighlightState);

    private VendorPurchaseView(
        bool isOpen,
        nint panel,
        nint hover,
        nint hoveredOfferElement,
        IReadOnlyList<Offer> offers,
        IReadOnlyList<PageControl> pages)
    {
        IsOpen = isOpen;
        Panel = panel;
        HoverElement = hover;
        HoveredOfferElement = hoveredOfferElement;
        Offers = offers;
        PageControls = pages;
    }

    public bool IsOpen { get; }
    public nint Panel { get; }
    public nint HoverElement { get; }
    public nint HoveredOfferElement { get; }
    public IReadOnlyList<Offer> Offers { get; }
    public IReadOnlyList<PageControl> PageControls { get; }
    public Offer? HoveredOffer => Offers.FirstOrDefault(x => x.Element == HoveredOfferElement);

    public static VendorPurchaseView Read(MemoryReader reader, nint ingameState)
    {
        reader.TryReadStruct<nint>(ingameState + KnownOffsets.IngameState.UIHover, out var hover);
        nint panel = 0;
        if (!reader.TryReadStruct<nint>(ingameState + KnownOffsets.IngameState.IngameUi, out var ingameUi)
            || ingameUi == 0
            || !reader.TryReadStruct<nint>(
                ingameUi + KnownOffsets.IngameUiElements.PurchaseWindow, out panel)
            || panel == 0
            || !ElementReader.IsVisibleDeep(reader, panel))
            return new VendorPurchaseView(false, panel, hover, 0, [], []);

        var offers = new List<Offer>();
        var pages = new List<PageControl>();
        var queue = new Queue<(nint Address, string Path, int Depth)>();
        var seen = new HashSet<nint>();
        var seenItems = new HashSet<nint>();
        queue.Enqueue((panel, "purchase", 0));

        while (queue.Count > 0 && seen.Count < 2_048)
        {
            var (address, path, depth) = queue.Dequeue();
            if (!seen.Add(address)) continue;
            var element = ElementReader.TryReadSnapshot(reader, address, 128);
            if (element is null) continue;

            var text = ReadElementText(reader, address).Trim();
            if (ElementReader.IsVisibleDeep(reader, address)
                && TryParsePage(text, out var page))
            {
                reader.TryReadStruct<ColorBGRA>(address + KnownOffsets.Element.LabelBackgroundColor, out var background);
                reader.TryReadStruct<ColorBGRA>(address + KnownOffsets.Element.LabelTextColor, out var textColor);
                reader.TryReadStruct<ColorBGRA>(address + KnownOffsets.Element.LabelBorderColor, out var border);
                reader.TryReadStruct<byte>(address + KnownOffsets.Element.ShinyHighlightState, out var highlight);
                pages.Add(new PageControl(
                    page,
                    address,
                    ElementGeometry.TryReadRect(reader, address),
                    text,
                    background,
                    textColor,
                    border,
                    highlight));
            }

            if (reader.TryReadStruct<nint>(address + KnownOffsets.NormalInventoryItem.Item, out var item)
                && item != 0
                && seenItems.Add(item))
            {
                var metadata = EntityListReader.ReadEntityPath(reader, item);
                if (metadata.StartsWith("Metadata/Items/", StringComparison.Ordinal))
                {
                    var components = EntityComponents.ReadComponentMap(reader, item);
                    reader.TryReadStruct<uint>(address + KnownOffsets.Element.Flags, out var elementFlags);
                    reader.TryReadStruct<nint>(address + KnownOffsets.Element.RenderedTooltip, out var renderedTooltip);
                    offers.Add(new Offer(
                        address,
                        item,
                        renderedTooltip,
                        path,
                        metadata,
                        ReadBaseName(reader, components),
                        ElementGeometry.TryReadRect(reader, address),
                        ElementReader.IsVisibleDeep(reader, address),
                        elementFlags,
                        ReadVisibleTextTree(reader, renderedTooltip),
                        ReadCostEntries(reader, renderedTooltip),
                        ReadRequirements(reader, components)));
                }
            }

            if (depth >= 14) continue;
            for (var i = 0; i < element.Children.Count; i++)
                queue.Enqueue((element.Children[i], $"{path}/{i}", depth + 1));
        }

        var hoveredOfferElement = ResolveHoveredOfferElement(reader, hover, offers);
        return new VendorPurchaseView(true, panel, hover, hoveredOfferElement, offers, pages
            .GroupBy(x => x.Page).Select(x => x.First()).OrderBy(x => x.Page).ToArray());
    }

    private static nint ResolveHoveredOfferElement(
        MemoryReader reader,
        nint hover,
        IReadOnlyList<Offer> offers)
    {
        if (hover == 0) return 0;
        var offerElements = offers.Select(x => x.Element).ToHashSet();
        var current = hover;
        for (var depth = 0; depth < 24 && current != 0; depth++)
        {
            if (offerElements.Contains(current)) return current;
            if (!reader.TryReadStruct<nint>(current + KnownOffsets.Element.Parent, out var parent)
                || parent == current)
                break;
            current = parent;
        }
        return 0;
    }

    private static Requirements ReadRequirements(
        MemoryReader reader,
        IReadOnlyDictionary<string, nint> components)
    {
        if (!components.TryGetValue("AttributeRequirements", out var component)
            || !reader.TryReadStruct<nint>(
                component + KnownOffsets.AttributeRequirementsComponent.Data, out var data)
            || data == 0)
            return new Requirements(0, 0, 0);
        reader.TryReadStruct<int>(data + KnownOffsets.AttributeRequirementsComponent.Strength, out var strength);
        reader.TryReadStruct<int>(data + KnownOffsets.AttributeRequirementsComponent.Dexterity, out var dexterity);
        reader.TryReadStruct<int>(data + KnownOffsets.AttributeRequirementsComponent.Intelligence, out var intelligence);
        return new Requirements(strength, dexterity, intelligence);
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

    private static IReadOnlyList<string> ReadVisibleTextTree(MemoryReader reader, nint root)
    {
        if (root == 0) return [];
        var lines = new List<string>();
        var queue = new Queue<(nint Address, int Depth)>();
        var seen = new HashSet<nint>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0 && seen.Count < 1_024)
        {
            var (address, depth) = queue.Dequeue();
            if (!seen.Add(address)) continue;
            var element = ElementReader.TryReadSnapshot(reader, address, 128);
            if (element is null) continue;
            if (ElementReader.IsVisibleDeep(reader, address))
            {
                var text = ReadElementText(reader, address);
                foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (!string.IsNullOrWhiteSpace(line)) lines.Add(line);
            }
            if (depth >= 14) continue;
            foreach (var child in element.Children) queue.Enqueue((child, depth + 1));
        }
        return lines;
    }

    private sealed record TextNode(
        nint Element,
        string Text,
        ElementGeometry.Rect Rect,
        ColorBGRA TextColor);

    private static IReadOnlyList<CostEntry> ReadCostEntries(MemoryReader reader, nint root)
    {
        if (root == 0) return [];
        var queue = new Queue<(nint Address, int Depth)>();
        var seen = new HashSet<nint>();
        queue.Enqueue((root, 0));
        var costNodes = new List<nint>();
        while (queue.Count > 0 && seen.Count < 1_024)
        {
            var (address, depth) = queue.Dequeue();
            if (!seen.Add(address)) continue;
            var element = ElementReader.TryReadSnapshot(reader, address, 128);
            if (element is null) continue;
            if (ElementReader.IsVisibleDeep(reader, address)
                && string.Equals(ReadElementText(reader, address).Trim(), "Cost:", StringComparison.OrdinalIgnoreCase))
                costNodes.Add(address);
            if (depth >= 14) continue;
            foreach (var child in element.Children) queue.Enqueue((child, depth + 1));
        }

        var result = new List<CostEntry>();
        foreach (var costNode in costNodes)
        {
            if (!reader.TryReadStruct<nint>(costNode + KnownOffsets.Element.Parent, out var container)
                || container == 0)
                continue;
            var nodes = ReadVisibleTextNodes(reader, container, maxDepth: 4);
            foreach (var countNode in nodes.Where(x => TryParseCount(x.Text, out _)))
            {
                TryParseCount(countNode.Text, out var count);
                var currency = nodes
                    .Where(x => x.Element != countNode.Element
                        && !string.Equals(x.Text.Trim(), "Cost:", StringComparison.OrdinalIgnoreCase)
                        && !TryParseCount(x.Text, out _)
                        && x.Rect.CenterX > countNode.Rect.CenterX
                        && MathF.Abs(x.Rect.CenterY - countNode.Rect.CenterY) <= 8f)
                    .OrderBy(x => x.Rect.CenterX - countNode.Rect.CenterX)
                    .FirstOrDefault();
                if (currency is not null)
                    result.Add(new CostEntry(
                        count,
                        currency.Text.Trim(),
                        countNode.TextColor,
                        currency.TextColor));
            }
        }
        return result.Distinct().ToArray();
    }

    private static IReadOnlyList<TextNode> ReadVisibleTextNodes(
        MemoryReader reader,
        nint root,
        int maxDepth)
    {
        var result = new List<TextNode>();
        var queue = new Queue<(nint Address, int Depth)>();
        var seen = new HashSet<nint>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0 && seen.Count < 128)
        {
            var (address, depth) = queue.Dequeue();
            if (!seen.Add(address)) continue;
            var element = ElementReader.TryReadSnapshot(reader, address, 64);
            if (element is null) continue;
            if (ElementReader.IsVisibleDeep(reader, address)
                && ElementGeometry.TryReadRect(reader, address) is { } rect)
            {
                var text = ReadElementText(reader, address).Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    reader.TryReadStruct<ColorBGRA>(
                        address + KnownOffsets.Element.LabelTextColor, out var textColor);
                    result.Add(new TextNode(address, text, rect, textColor));
                }
            }
            if (depth >= maxDepth) continue;
            foreach (var child in element.Children) queue.Enqueue((child, depth + 1));
        }
        return result;
    }

    private static bool TryParseCount(string text, out int count)
    {
        var value = text.Trim();
        count = 0;
        return value.Length > 1
            && value.EndsWith('x')
            && int.TryParse(value[..^1], out count)
            && count > 0;
    }

    private static string ReadElementText(MemoryReader reader, nint element)
    {
        var text = NativeString.Read(reader, element + KnownOffsets.Element.TextNoTags);
        return string.IsNullOrWhiteSpace(text)
            ? NativeString.Read(reader, element + KnownOffsets.Element.Text)
            : text;
    }

    private static bool TryParsePage(string text, out int page)
    {
        page = 0;
        return text.Length >= 3
            && text[0] == '-'
            && text[^1] == '-'
            && int.TryParse(text[1..^1], out page)
            && page > 0;
    }
}
