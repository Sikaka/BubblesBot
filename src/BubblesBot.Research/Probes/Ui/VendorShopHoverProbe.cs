using BubblesBot.Core;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;
using BubblesBot.Research.Probing;
using System.Text;

namespace BubblesBot.Research.Probes.Ui;

/// <summary>
/// Read-only capture for a normal NPC purchase window while an offer is hovered. This establishes
/// the evidence needed before a purchase LiveTest is allowed to click: panel identity, offer item
/// entity, click geometry, hover ancestry, tooltip text, and the item's flattened stat records.
/// </summary>
public sealed class VendorShopHoverProbe : IProbe
{
    public string Name => "ui.vendor-shop-hover";
    public string Group => "ui";
    public string Description => "Capture a hovered NPC vendor offer, tooltip, stats, cost text, and click geometry without input.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx) => Capture(ctx);
    public ProbeResult Discover(ProbeContext ctx) => Capture(ctx);

    private static ProbeResult Capture(ProbeContext ctx)
    {
        var reader = ctx.Reader;
        var expectedName = GetOption(ctx.Arguments, "--expect-name");
        var expectedBase = GetOption(ctx.Arguments, "--expect-base");
        var expectedEvasion = GetIntOption(ctx.Arguments, "--expect-evasion");
        var expectedDexterity = GetIntOption(ctx.Arguments, "--expect-dexterity");
        var expectedCostCount = GetIntOption(ctx.Arguments, "--expect-cost-count");
        var expectedCostCurrency = GetOption(ctx.Arguments, "--expect-cost-currency");
        if (!reader.TryReadStruct<nint>(
                ctx.Chain.IngameState + KnownOffsets.IngameState.IngameUi, out var ingameUi)
            || ingameUi == 0)
            return ProbeResult.Fail("IngameUi did not resolve");

        var panels = OpenPanelsView.FromIngameUi(reader, ctx.Chain.IngameState);
        if (!panels.IsOpen("PurchaseWindow"))
            return ProbeResult.Skip("PurchaseWindow is not open; open an NPC shop and hover one offer");
        if (!panels.IsOpen("InventoryPanel"))
            return ProbeResult.Fail("PurchaseWindow is open but InventoryPanel is not open");

        if (!reader.TryReadStruct<nint>(
                ingameUi + KnownOffsets.IngameUiElements.PurchaseWindow, out var purchasePanel)
            || purchasePanel == 0
            || ElementReader.TryReadSnapshot(reader, purchasePanel, 256) is null)
            return ProbeResult.Fail("PurchaseWindow pointer is null or not a sound Element");

        reader.TryReadStruct<nint>(
            ctx.Chain.IngameState + KnownOffsets.IngameState.UIHover, out var uiHover);

        var offers = ReadOffers(reader, purchasePanel);
        if (offers.Count == 0)
            return ProbeResult.Fail("PurchaseWindow tree contained no resolvable item offers");

        var hovered = offers
            .Where(x => x.Tooltip != 0 && ElementReader.IsVisibleDeep(reader, x.Tooltip))
            .ToArray();

        // Some tooltip roots do not participate in the normal parent visibility chain. If that
        // signal is absent, tie UIHover to an offer by walking its ancestors.
        if (hovered.Length == 0 && uiHover != 0)
        {
            var hoverAncestors = ReadAncestors(reader, uiHover).Select(x => x.Address).ToHashSet();
            hovered = offers.Where(x => hoverAncestors.Contains(x.Element)).ToArray();
        }

        var lines = new List<string>
        {
            $"openPanels=[{string.Join(", ", panels.Open)}]",
            $"PurchaseWindow=0x{(long)purchasePanel:X}",
            $"UIHover=0x{(long)uiHover:X}",
            $"offers={offers.Count} hoveredCandidates={hovered.Length}",
        };
        var vendorView = VendorPurchaseView.Read(reader, ctx.Chain.IngameState);
        foreach (var page in vendorView.PageControls)
        {
            lines.Add($"pageControl page={page.Page} element=0x{(long)page.Element:X} rect={FormatRect(page.Rect)} "
                + $"background={FormatColor(page.BackgroundColor)} textColor={FormatColor(page.TextColor)} "
                + $"border={FormatColor(page.BorderColor)} highlight={page.HighlightState}");
        }

        if (uiHover == 0)
        {
            var expectedMatches = (string.IsNullOrWhiteSpace(expectedBase)
                ? offers.Take(8)
                : offers.Where(x => string.Equals(x.BaseName, expectedBase, StringComparison.Ordinal)).Take(8)).ToArray();
            lines.Add(string.IsNullOrWhiteSpace(expectedBase)
                ? "sample offers:"
                : $"offers matching expected base '{expectedBase}':");
            foreach (var offer in expectedMatches) lines.Add(FormatOffer(offer));
            if (expectedMatches.Length == 1)
            {
                if (!string.IsNullOrWhiteSpace(expectedName))
                    AppendExpectedNameDiscovery(reader, expectedMatches[0], expectedName, lines);
                AppendExpectedValueDiscovery(
                    reader, expectedMatches[0], expectedEvasion, expectedDexterity, expectedCostCount, lines);
            }
            return ProbeResult.Fail("UIHover is null while an offer is expected to be hovered"
                + Environment.NewLine + string.Join(Environment.NewLine, lines));
        }

        if (uiHover != 0)
        {
            lines.Add("hover ancestry:");
            foreach (var ancestor in ReadAncestors(reader, uiHover))
            {
                lines.Add($"  depth={ancestor.Depth} addr=0x{(long)ancestor.Address:X} "
                    + $"childIndex={ancestor.ChildIndex} visible={ancestor.Visible} "
                    + $"rect={FormatRect(ancestor.Rect)} text='{OneLine(ancestor.Text)}'");
            }
            AppendIngameStateElementCandidates(reader, ctx.Chain.IngameState, lines);
        }

        var visibleTexts = ReadVisibleTextNodes(reader, ctx.Chain.IngameState);
        lines.Add($"visibleUiTextNodes={visibleTexts.Count}");
        foreach (var node in visibleTexts.Take(512))
            lines.Add($"  ui-text addr=0x{(long)node.Address:X} rect={FormatRect(node.Rect)} text='{OneLine(node.Text)}'");

        var selected = hovered.Length == 1 ? hovered[0] : null;
        if (selected is not null)
        {
            lines.Add("hovered offer:");
            lines.Add(FormatOffer(selected));
            AppendElementSubtree(reader, selected.Element, lines);
            AppendPointerElementCandidates(reader, selected.Element, 0x800, "hovered element", lines);
            if (reader.TryReadStruct<nint>(selected.Element + KnownOffsets.Element.RenderedTooltip, out var tooltipElementCandidate)
                && tooltipElementCandidate != 0)
            {
                lines.Add("candidate rendered tooltip at hovered element +0x4E8:");
                AppendElementSubtree(reader, tooltipElementCandidate, lines);
            }
            var tooltipTexts = ReadTextTree(reader, selected.Tooltip);
            lines.Add($"tooltipTextNodes={tooltipTexts.Count}");
            foreach (var text in tooltipTexts)
                lines.Add($"  tooltip: {OneLine(text)}");
            var tooltipStrings = ReadUtf16Runs(reader, selected.Tooltip, 0x2800);
            lines.Add($"tooltipUtf16Runs={tooltipStrings.Count}");
            foreach (var (offset, text) in tooltipStrings.Take(256))
                lines.Add($"  tooltip+0x{offset:X}: {OneLine(text)}");
            if (!string.IsNullOrWhiteSpace(expectedName))
            {
                AppendExpectedNameDiscovery(reader, selected, expectedName, lines);
            }
            AppendExpectedValueDiscovery(
                reader, selected, expectedEvasion, expectedDexterity, expectedCostCount, lines);
        }
        else
        {
            lines.Add("offer summary:");
            foreach (var offer in offers.Take(40))
                lines.Add(FormatOffer(offer));
        }

        var evidence = string.Join(Environment.NewLine, lines);
        if (hovered.Length == 0)
            return ProbeResult.Fail("no offer could be tied to the visible hover/tooltip" + Environment.NewLine + evidence);
        if (hovered.Length > 1)
            return ProbeResult.Fail($"hover identity is ambiguous across {hovered.Length} offers" + Environment.NewLine + evidence);
        if (selected is null || string.IsNullOrWhiteSpace(selected.BaseName))
            return ProbeResult.Fail("hovered offer resolved without a base name" + Environment.NewLine + evidence);
        if (!string.IsNullOrWhiteSpace(expectedBase)
            && !string.Equals(selected.BaseName, expectedBase, StringComparison.Ordinal))
            return ProbeResult.Fail($"expected base '{expectedBase}' but resolved '{selected.BaseName}'" + Environment.NewLine + evidence);
        if (selected.Rect is null)
            return ProbeResult.Fail("hovered offer has no click rectangle" + Environment.NewLine + evidence);

        var texts = ReadTextTree(reader, selected.Tooltip);
        var renderedTexts = ReadTextTree(reader, selected.RenderedTooltip);
        var runs = ReadUtf16Runs(reader, selected.Tooltip, 0x2800);
        var renderedText = string.Join('\n', renderedTexts);
        var hasBaseIdentity = renderedText.Contains(selected.BaseName, StringComparison.OrdinalIgnoreCase);
        var hasReadableTooltipEvidence = renderedTexts.Count > 0 || texts.Count > 0 || runs.Any(x =>
            x.Text.Contains(selected.BaseName, StringComparison.OrdinalIgnoreCase)
            || selected.Stats.Any(stat => x.Text.Contains(stat.Value.ToString(), StringComparison.Ordinal)));
        if (!hasReadableTooltipEvidence)
            return ProbeResult.Fail("hovered offer tooltip exposed no readable text" + Environment.NewLine + evidence);
        if (!hasBaseIdentity)
            return ProbeResult.Fail("rendered tooltip did not independently identify the hovered item base" + Environment.NewLine + evidence);
        if (!string.IsNullOrWhiteSpace(expectedName)
            && !renderedText.Contains(expectedName, StringComparison.OrdinalIgnoreCase))
            return ProbeResult.Fail($"rendered tooltip did not contain expected name '{expectedName}'" + Environment.NewLine + evidence);
        if (expectedEvasion is { } evasion
            && !renderedText.Contains($"Evasion Rating: {evasion}", StringComparison.OrdinalIgnoreCase))
            return ProbeResult.Fail($"rendered tooltip did not contain expected evasion {evasion}" + Environment.NewLine + evidence);
        if (expectedDexterity is { } dexterity
            && !renderedText.Contains($"{dexterity} Dex", StringComparison.OrdinalIgnoreCase))
            return ProbeResult.Fail($"rendered tooltip did not contain expected Dexterity {dexterity}" + Environment.NewLine + evidence);
        if (expectedCostCount is { } count
            && !renderedText.Contains($"{count}x", StringComparison.OrdinalIgnoreCase))
            return ProbeResult.Fail($"rendered tooltip did not contain expected cost count {count}" + Environment.NewLine + evidence);
        if (!string.IsNullOrWhiteSpace(expectedCostCurrency)
            && !renderedText.Contains(expectedCostCurrency, StringComparison.OrdinalIgnoreCase))
            return ProbeResult.Fail($"rendered tooltip did not contain expected cost currency '{expectedCostCurrency}'" + Environment.NewLine + evidence);

        return ProbeResult.Pass(evidence);
    }

    private static string? GetOption(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i + 1 < args.Count; i++)
            if (string.Equals(args[i], name, StringComparison.Ordinal))
                return args[i + 1];
        return null;
    }

    private static int? GetIntOption(IReadOnlyList<string> args, string name)
        => int.TryParse(GetOption(args, name), out var value) ? value : null;

    private static void AppendExpectedNameDiscovery(
        MemoryReader reader,
        Offer offer,
        string expectedName,
        List<string> lines)
    {
        var components = EntityComponents.ReadComponentMap(reader, offer.Item);
        lines.Add("selected offer components: " + string.Join(", ", components
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => $"{x.Key}=0x{(long)x.Value:X}")));
        var anchors = new List<(string Name, nint Address, int Window)>
        {
            ("item", offer.Item, 0x800),
            ("tooltip", offer.Tooltip, 0x2800),
        };
        anchors.AddRange(components.Select(x => ($"component:{x.Key}", x.Value, 0x1000)));
        if (components.TryGetValue("Mods", out var mods)
            && reader.TryReadStruct<nint>(mods + KnownOffsets.ModsComponent.UniqueName, out var uniqueNameData)
            && uniqueNameData != 0)
            anchors.Add(("mods.unique-name-data", uniqueNameData, 0x1000));
        if (components.TryGetValue("Base", out var baseAddress)
            && reader.TryReadStruct<nint>(baseAddress + KnownOffsets.BaseComponent.ItemInfo, out var itemInfo)
            && itemInfo != 0)
            anchors.Add(("base.item-info", itemInfo, 0x800));
        var needle = Encoding.Unicode.GetBytes(expectedName);
        var findings = new List<string>();
        foreach (var (anchorName, anchor, window) in anchors)
        {
            var bytes = new byte[window];
            var read = reader.TryReadBytes(anchor, bytes);
            for (var offset = 0; offset + needle.Length <= read; offset += 2)
            {
                if (bytes.AsSpan(offset, needle.Length).SequenceEqual(needle))
                    findings.Add($"inline {anchorName}+0x{offset:X}");
            }
            for (var offset = 0; offset + 8 <= read; offset += 8)
            {
                var pointer = (nint)BitConverter.ToInt64(bytes, offset);
                if (!PlausiblePointer(pointer)) continue;
                var direct = reader.ReadStringUtf16(pointer, Math.Min(256, expectedName.Length + 8));
                if (string.Equals(direct, expectedName, StringComparison.Ordinal))
                    findings.Add($"pointer {anchorName}+0x{offset:X} -> 0x{(long)pointer:X}");
                var native = NativeString.Read(reader, pointer, 256);
                if (string.Equals(native, expectedName, StringComparison.Ordinal))
                    findings.Add($"NativeString* {anchorName}+0x{offset:X} -> 0x{(long)pointer:X}");
            }
        }
        lines.Add($"expectedName='{expectedName}' boundedFindings={findings.Count}");
        foreach (var finding in findings.Distinct(StringComparer.Ordinal))
            lines.Add($"  {finding}");
    }

    private static bool PlausiblePointer(nint pointer)
    {
        var value = (long)pointer;
        return value > 0x10000 && value < 0x7FFF_FFFF_FFFF;
    }

    private static void AppendExpectedValueDiscovery(
        MemoryReader reader,
        Offer offer,
        int? evasion,
        int? dexterity,
        int? costCount,
        List<string> lines)
    {
        var components = EntityComponents.ReadComponentMap(reader, offer.Item);
        if (evasion is { } ev && components.TryGetValue("Armour", out var armour))
        {
            AppendNumericHits(reader, armour, 0x400, ev, "Armour", "evasion", lines);
            if (reader.TryReadStruct<nint>(armour + 0x10, out var armourData) && armourData != 0)
                AppendNumericHits(reader, armourData, 0x100, ev, "Armour.+0x10 data", "evasion", lines);
        }
        if (dexterity is { } dex && components.TryGetValue("AttributeRequirements", out var requirements))
        {
            AppendNumericHits(reader, requirements, 0x400, dex, "AttributeRequirements", "dexterity", lines);
            if (reader.TryReadStruct<nint>(requirements + 0x10, out var requirementData) && requirementData != 0)
                AppendNumericHits(reader, requirementData, 0x100, dex, "AttributeRequirements.+0x10 data", "dexterity", lines);
        }
        if (dexterity is { } baseDex
            && components.TryGetValue("Base", out var baseAddress)
            && reader.TryReadStruct<nint>(baseAddress + KnownOffsets.BaseComponent.ItemInfo, out var itemInfo)
            && itemInfo != 0)
            AppendNumericHits(reader, itemInfo, 0x400, baseDex, "Base.ItemInfo", "dexterity", lines);
        if (components.TryGetValue("Base", out var baseComponent))
            lines.Add($"Base.PublicPrice='{OneLine(NativeString.Read(reader, baseComponent + KnownOffsets.BaseComponent.PublicPrice))}'");
        if (components.TryGetValue("Mods", out var mods))
        {
            var inlineName = NativeString.Read(reader, mods + KnownOffsets.ModsComponent.UniqueName);
            reader.TryReadStruct<nint>(mods + KnownOffsets.ModsComponent.UniqueName, out var namePointer);
            var pointedName = namePointer == 0 ? string.Empty : NativeString.Read(reader, namePointer);
            lines.Add($"Mods.UniqueName inline='{OneLine(inlineName)}' pointer=0x{(long)namePointer:X} pointed='{OneLine(pointedName)}'");
        }
        if (costCount is { } cost)
        {
            AppendNumericHits(reader, offer.Element, 0x800, cost, "offer element", "cost-count", lines);
            AppendNumericHits(reader, offer.Tooltip, 0x2800, cost, "tooltip data", "cost-count", lines);
        }
    }

    private static void AppendNumericHits(
        MemoryReader reader,
        nint address,
        int window,
        int expected,
        string anchor,
        string label,
        List<string> lines)
    {
        var bytes = new byte[window];
        var read = reader.TryReadBytes(address, bytes);
        var byteHits = new List<int>();
        var shortHits = new List<int>();
        var intHits = new List<int>();
        if (expected is >= byte.MinValue and <= byte.MaxValue)
            for (var offset = 0; offset < read; offset++)
                if (bytes[offset] == expected) byteHits.Add(offset);
        if (expected is >= short.MinValue and <= short.MaxValue)
            for (var offset = 0; offset + 2 <= read; offset += 2)
                if (BitConverter.ToInt16(bytes, offset) == expected) shortHits.Add(offset);
        for (var offset = 0; offset + 4 <= read; offset += 4)
            if (BitConverter.ToInt32(bytes, offset) == expected) intHits.Add(offset);
        lines.Add($"expected {label}={expected} in {anchor}@0x{(long)address:X}: "
            + $"byte=[{FormatHits(byteHits)}] int16=[{FormatHits(shortHits)}] int32=[{FormatHits(intHits)}]");
    }

    private static string FormatHits(IReadOnlyList<int> hits)
        => hits.Count == 0 ? "none" : string.Join(", ", hits.Take(64).Select(x => $"+0x{x:X}"))
            + (hits.Count > 64 ? $", ... ({hits.Count} total)" : string.Empty);

    private static void AppendElementSubtree(MemoryReader reader, nint root, List<string> lines)
    {
        lines.Add("hovered element subtree:");
        var queue = new Queue<(nint Address, string Path, int Depth)>();
        var seen = new HashSet<nint>();
        queue.Enqueue((root, "hover", 0));
        while (queue.Count > 0 && seen.Count < 256)
        {
            var (address, path, depth) = queue.Dequeue();
            if (!seen.Add(address)) continue;
            var element = ElementReader.TryReadSnapshot(reader, address, 128);
            if (element is null) continue;
            reader.TryReadStruct<nint>(address + KnownOffsets.NormalInventoryItem.Item, out var item);
            reader.TryReadStruct<nint>(address + KnownOffsets.Element.Tooltip, out var tooltip);
            var itemPath = item == 0 ? string.Empty : EntityListReader.ReadEntityPath(reader, item);
            lines.Add($"  {path} addr=0x{(long)address:X} visible={ElementReader.IsVisibleDeep(reader, address)} "
                + $"children={element.Children.Count} rect={FormatRect(ElementGeometry.TryReadRect(reader, address))} "
                + $"text='{OneLine(ReadElementText(reader, address))}' item=0x{(long)item:X} "
                + $"itemPath='{OneLine(itemPath)}' tooltip=0x{(long)tooltip:X}");
            if (depth >= 5) continue;
            for (var i = 0; i < element.Children.Count; i++)
                queue.Enqueue((element.Children[i], $"{path}/{i}", depth + 1));
        }
    }

    private static void AppendIngameStateElementCandidates(
        MemoryReader reader,
        nint ingameState,
        List<string> lines)
    {
        lines.Add("IngameState element-pointer candidates:");
        for (var offset = 0x480; offset <= 0x700; offset += 8)
        {
            if (!reader.TryReadStruct<nint>(ingameState + offset, out var address) || address == 0)
                continue;
            var element = ElementReader.TryReadSnapshot(reader, address, 256);
            if (element is null) continue;
            lines.Add($"  +0x{offset:X}=0x{(long)address:X} visible={ElementReader.IsVisibleDeep(reader, address)} "
                + $"children={element.Children.Count} rect={FormatRect(ElementGeometry.TryReadRect(reader, address))} "
                + $"text='{OneLine(ReadElementText(reader, address))}'");
        }
    }

    private static void AppendPointerElementCandidates(
        MemoryReader reader,
        nint owner,
        int window,
        string label,
        List<string> lines)
    {
        lines.Add($"{label} embedded Element-pointer candidates:");
        for (var offset = 0; offset < window; offset += 8)
        {
            if (!reader.TryReadStruct<nint>(owner + offset, out var address) || address == 0)
                continue;
            var element = ElementReader.TryReadSnapshot(reader, address, 256);
            if (element is null) continue;
            lines.Add($"  +0x{offset:X}=0x{(long)address:X} visible={ElementReader.IsVisibleDeep(reader, address)} "
                + $"children={element.Children.Count} rect={FormatRect(ElementGeometry.TryReadRect(reader, address))} "
                + $"text='{OneLine(ReadElementText(reader, address))}'");
        }
    }

    private sealed record Offer(
        nint Element,
        nint Item,
        nint Tooltip,
        nint RenderedTooltip,
        string TreePath,
        string Metadata,
        string BaseName,
        int Rarity,
        bool Identified,
        int ItemLevel,
        int RequiredLevel,
        ElementGeometry.Rect? Rect,
        IReadOnlyList<(int Id, int Value)> Stats);

    private static List<Offer> ReadOffers(MemoryReader reader, nint panel)
    {
        var result = new List<Offer>();
        var queue = new Queue<(nint Address, string Path, int Depth)>();
        var seenElements = new HashSet<nint>();
        var seenItems = new HashSet<nint>();
        queue.Enqueue((panel, "purchase", 0));

        while (queue.Count > 0 && seenElements.Count < 2_048)
        {
            var (address, path, depth) = queue.Dequeue();
            if (!seenElements.Add(address)) continue;
            var element = ElementReader.TryReadSnapshot(reader, address, maxChildren: 128);
            if (element is null) continue;

            if (reader.TryReadStruct<nint>(address + KnownOffsets.NormalInventoryItem.Item, out var item)
                && item != 0
                && seenItems.Add(item))
            {
                var metadata = EntityListReader.ReadEntityPath(reader, item);
                if (metadata.StartsWith("Metadata/Items/", StringComparison.Ordinal))
                {
                    var components = EntityComponents.ReadComponentMap(reader, item);
                    var baseName = ReadBaseName(reader, components);
                    var rarity = -1;
                    var identified = false;
                    var itemLevel = 0;
                    var requiredLevel = 0;
                    if (components.TryGetValue("Mods", out var mods))
                    {
                        reader.TryReadStruct(mods + KnownOffsets.ModsComponent.ItemRarity, out rarity);
                        reader.TryReadStruct(mods + KnownOffsets.ModsComponent.Identified, out identified);
                        reader.TryReadStruct(mods + KnownOffsets.ModsComponent.ItemLevel, out itemLevel);
                        reader.TryReadStruct(mods + KnownOffsets.ModsComponent.RequiredLevel, out requiredLevel);
                    }
                    reader.TryReadStruct<nint>(address + KnownOffsets.Element.Tooltip, out var tooltip);
                    reader.TryReadStruct<nint>(address + KnownOffsets.Element.RenderedTooltip, out var renderedTooltip);
                    result.Add(new Offer(
                        address,
                        item,
                        tooltip,
                        renderedTooltip,
                        path,
                        metadata,
                        baseName,
                        rarity,
                        identified,
                        itemLevel,
                        requiredLevel,
                        ElementGeometry.TryReadRect(reader, address),
                        ItemStatsReader.Read(reader, item)));
                }
            }

            if (depth >= 14) continue;
            for (var i = 0; i < element.Children.Count; i++)
                queue.Enqueue((element.Children[i], $"{path}/{i}", depth + 1));
        }
        return result;
    }

    private static string ReadBaseName(MemoryReader reader, IReadOnlyDictionary<string, nint> components)
    {
        if (!components.TryGetValue("Base", out var baseAddress)
            || !reader.TryReadStruct<nint>(baseAddress + KnownOffsets.BaseComponent.ItemInfo, out var info)
            || info == 0)
            return string.Empty;
        return NativeString.Read(reader, info + KnownOffsets.ItemInfo.BaseName);
    }

    private static string FormatOffer(Offer offer)
        => $"  path={offer.TreePath} element=0x{(long)offer.Element:X} item=0x{(long)offer.Item:X} "
           + $"tooltip=0x{(long)offer.Tooltip:X} rect={FormatRect(offer.Rect)} "
           + $"renderedTooltip=0x{(long)offer.RenderedTooltip:X} "
           + $"base='{offer.BaseName}' rarity={offer.Rarity} identified={offer.Identified} "
           + $"ilvl={offer.ItemLevel} requiredLevel={offer.RequiredLevel} metadata='{offer.Metadata}' "
           + $"stats=[{string.Join(", ", offer.Stats.Select(x => $"{x.Id}={x.Value}"))}]";

    private sealed record Ancestor(
        int Depth,
        nint Address,
        int ChildIndex,
        bool Visible,
        ElementGeometry.Rect? Rect,
        string Text);

    private static IReadOnlyList<Ancestor> ReadAncestors(MemoryReader reader, nint start)
    {
        var result = new List<Ancestor>();
        var seen = new HashSet<nint>();
        var address = start;
        for (var depth = 0; depth < 32 && address != 0 && seen.Add(address); depth++)
        {
            var text = ReadElementText(reader, address);
            var childIndex = -1;
            if (!reader.TryReadStruct<nint>(address + KnownOffsets.Element.Parent, out var parent))
                parent = 0;
            if (parent != 0 && ElementReader.TryReadSnapshot(reader, parent, 256) is { } parentSnapshot)
                childIndex = parentSnapshot.Children.ToList().FindIndex(x => x == address);
            result.Add(new Ancestor(
                depth,
                address,
                childIndex,
                ElementReader.IsVisibleDeep(reader, address),
                ElementGeometry.TryReadRect(reader, address),
                text));
            address = parent;
        }
        return result;
    }

    private static IReadOnlyList<string> ReadTextTree(MemoryReader reader, nint root)
    {
        var result = new List<string>();
        if (root == 0) return result;
        var queue = new Queue<(nint Address, int Depth)>();
        var seen = new HashSet<nint>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0 && seen.Count < 1_024)
        {
            var (address, depth) = queue.Dequeue();
            if (!seen.Add(address)) continue;
            var element = ElementReader.TryReadSnapshot(reader, address, 128);
            if (element is null) continue;
            var text = ReadElementText(reader, address);
            if (!string.IsNullOrWhiteSpace(text)) result.Add(text.Trim());
            if (depth >= 14) continue;
            foreach (var child in element.Children) queue.Enqueue((child, depth + 1));
        }
        return result;
    }

    private static IReadOnlyList<(int Offset, string Text)> ReadUtf16Runs(
        MemoryReader reader,
        nint address,
        int maxBytes)
    {
        var bytes = new byte[maxBytes];
        var read = reader.TryReadBytes(address, bytes);
        if (read < 2) return [];

        var result = new List<(int, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var start = -1;
        var chars = new List<char>(128);
        void Flush()
        {
            if (start >= 0 && chars.Count >= 3)
            {
                var value = new string(chars.ToArray()).Trim();
                if (value.Length >= 3 && seen.Add(value)) result.Add((start, value));
            }
            start = -1;
            chars.Clear();
        }

        for (var i = 0; i + 1 < read; i += 2)
        {
            var c = (char)(bytes[i] | bytes[i + 1] << 8);
            var printable = c is '\r' or '\n' or '\t' || c is >= (char)0x20 and <= (char)0x7E;
            if (c == '\0' || !printable)
            {
                Flush();
                continue;
            }
            if (start < 0) start = i;
            chars.Add(c);
            if (chars.Count >= 2_048) Flush();
        }
        Flush();
        return result;
    }

    private sealed record VisibleTextNode(nint Address, ElementGeometry.Rect? Rect, string Text);

    private static IReadOnlyList<VisibleTextNode> ReadVisibleTextNodes(
        MemoryReader reader,
        nint ingameState)
    {
        if (!reader.TryReadStruct<nint>(ingameState + KnownOffsets.IngameState.UIRoot, out var root)
            || root == 0)
            return [];

        var result = new List<VisibleTextNode>();
        var queue = new Queue<(nint Address, int Depth)>();
        var seen = new HashSet<nint>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0 && seen.Count < 20_000)
        {
            var (address, depth) = queue.Dequeue();
            if (!seen.Add(address)) continue;
            var element = ElementReader.TryReadSnapshot(reader, address, 512);
            if (element is null) continue;
            if (ElementReader.IsVisibleDeep(reader, address))
            {
                var text = ReadElementText(reader, address);
                if (!string.IsNullOrWhiteSpace(text))
                    result.Add(new VisibleTextNode(address, ElementGeometry.TryReadRect(reader, address), text.Trim()));
            }
            if (depth >= 24) continue;
            foreach (var child in element.Children) queue.Enqueue((child, depth + 1));
        }
        return result;
    }

    private static string ReadElementText(MemoryReader reader, nint element)
    {
        var text = NativeString.Read(reader, element + KnownOffsets.Element.TextNoTags);
        return string.IsNullOrWhiteSpace(text)
            ? NativeString.Read(reader, element + KnownOffsets.Element.Text)
            : text;
    }

    private static string FormatRect(ElementGeometry.Rect? rect)
        => rect is { } r ? $"{r.X:F0},{r.Y:F0},{r.Width:F0},{r.Height:F0}" : "none";

    private static string FormatColor(ColorBGRA color)
        => $"{color.B},{color.G},{color.R},{color.A}";

    private static string OneLine(string text)
        => text.Replace('\r', ' ').Replace('\n', '|').Trim();
}
