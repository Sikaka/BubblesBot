using BubblesBot.Bot.Input;
using BubblesBot.Core;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Reversible inventory -> sell offer -> inventory Ctrl+click contract.</summary>
public sealed class SellOfferCtrlClickRoundTripLiveTest : ILiveTestCase
{
    private const int LeftControlVk = 0xA2;
    private static readonly IReadOnlySet<string> Allowed =
        new HashSet<string>(StringComparer.Ordinal) { "SellWindow" };

    public string Id => "H-06-sell-offer-ctrl-roundtrip";
    public string Name => "Inventory/sell-offer Ctrl+click round trip";
    public string Description => "Offers one unique equipment item, proves exact item/proceeds identities and deltas, then withdraws it without accepting and restores the empty offer and inventory.";
    public string ManualSetup => "Open an NPC Sell Items window with both offer regions empty. Carry at least one uniquely pathed non-stackable weapon or armour and hold nothing on the cursor.";
    public LiveTestMutation Mutation => LiveTestMutation.Reversible;
    public bool DrivesInput => true;
    public IReadOnlySet<string> AllowedBlockingPanels => Allowed;

    public async Task<LiveTestCaseResult> RunAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        var baseline = context.Snapshot();
        var sell = ReadSell(baseline);
        var inventory = baseline.Inventory;
        context.Check(sell.IsOpen && inventory.IsOpen, "prepared sell/inventory panels",
            $"sell={sell.IsOpen} inventory={inventory.IsOpen}");
        context.Check(sell.PlayerOffer.Count == 0 && sell.VendorOffer.Count == 0,
            "prepared empty offer",
            $"playerItems={sell.PlayerOffer.Count} vendorItems={sell.VendorOffer.Count}");
        context.Check(sell.Accept?.Rect is not null && sell.Cancel?.Rect is not null,
            "sell controls readable",
            $"accept={sell.Accept?.Rect} cancel={sell.Cancel?.Rect}");
        context.Check(baseline.Cursor.Action == CursorView.CursorAction.Free,
            "prepared free cursor", FormatCursor(baseline.Cursor));
        if (!sell.IsOpen || !inventory.IsOpen || sell.PlayerOffer.Count != 0
            || sell.VendorOffer.Count != 0 || sell.Accept?.Rect is null || sell.Cancel?.Rect is null
            || baseline.Cursor.Action != CursorView.CursorAction.Free)
            return LiveTestCaseResult.Blocked("requires a readable empty sell offer and free cursor", "PreparedStateMismatch");

        var candidates = inventory.Items
            .Where(x => x.StackSize == 1 && x.Rect is { Width: > 0, Height: > 0 }
                && (x.Path.StartsWith("Metadata/Items/Weapons/", StringComparison.Ordinal)
                    || x.Path.StartsWith("Metadata/Items/Armours/", StringComparison.Ordinal)))
            .Where(x => inventory.Items.Count(y => string.Equals(y.Path, x.Path, StringComparison.Ordinal)) == 1)
            .OrderBy(x => x.OccupiedCells).ThenBy(x => x.Path, StringComparer.Ordinal).ToArray();
        context.Check(candidates.Length > 0, "unique sell candidate",
            candidates.Length == 0 ? "none" : string.Join(", ", candidates.Select(x => x.Path)));
        if (candidates.Length == 0 || candidates[0].Rect is not { } sourceRect)
            return LiveTestCaseResult.Blocked("no safe unique equipment sell candidate", "NoSafeOfferCandidate");

        var target = candidates[0];
        var targetBaseName = ReadBaseName(baseline.Reader, target.ItemEntity);
        var inventoryBefore = InventoryFingerprint(inventory);
        var sellBefore = SellFingerprint(sell);
        context.Check(!string.IsNullOrWhiteSpace(targetBaseName), "sell target identity",
            $"base='{targetBaseName}' metadata='{target.Path}' stack={target.StackSize} size={target.Width}x{target.Height}");
        if (string.IsNullOrWhiteSpace(targetBaseName))
            return LiveTestCaseResult.Blocked("sell candidate base identity is unreadable", "CandidateUnreadable");

        if (!await VerifyInventoryHoverAsync(context, target, targetBaseName, "inventory sell source", cancellationToken))
            return LiveTestCaseResult.Fail("sell source failed exact hover/tooltip identity", "TooltipMismatch");

        var point = context.Snapshot().Window.ToScreen(sourceRect.CenterX, sourceRect.CenterY);
        var offer = await context.VerifiedModifierClickAsync(
            point.X, point.Y, [LeftControlVk], ClickIntent.InteractUi,
            $"Ctrl+click inventory -> sell offer '{targetBaseName}'",
            () =>
            {
                var current = context.Snapshot();
                var currentSell = ReadSell(current);
                return currentSell.IsOpen
                    && current.Cursor.Action == CursorView.CursorAction.Free
                    && string.Equals(InventoryFingerprint(current.Inventory), inventoryBefore, StringComparison.Ordinal)
                    && currentSell.PlayerOffer.Count(x => string.Equals(x.Metadata, target.Path, StringComparison.Ordinal)) == 1;
            }, 4_000, cancellationToken);
        if (!await context.WaitForInputIdleAsync("after inventory-to-sell-offer transfer", 1_500, cancellationToken))
            return await FailWithRecoveryAsync(context, target.Path, targetBaseName, inventoryBefore,
                sellBefore, "input did not settle after offer entry", "InputSettleFailed", cancellationToken);
        if (offer != ActionOutcome.Confirmed)
            return await FailWithRecoveryAsync(context, target.Path, targetBaseName, inventoryBefore,
                sellBefore, "offer entry lacked exact item and proceeds deltas", "OfferOutcomeMismatch", cancellationToken);

        var offeredSnapshot = context.Snapshot();
        var offered = ReadSell(offeredSnapshot);
        var offeredMatches = offered.PlayerOffer
            .Where(x => string.Equals(x.Metadata, target.Path, StringComparison.Ordinal)).ToArray();
        context.Check(offeredMatches.Length == 1, "exact item entered Your Offer",
            $"metadata='{target.Path}' matches={offeredMatches.Length}");
        var offeredBaseName = offeredMatches.Length == 1 ? offeredMatches[0].BaseName : string.Empty;
        context.Check(offeredMatches.Length == 1
                && string.Equals(offeredBaseName, targetBaseName, StringComparison.Ordinal)
                && offeredMatches[0].StackSize == target.StackSize
                && offeredMatches[0].Width == target.Width
                && offeredMatches[0].Height == target.Height,
            "durable offered item identity",
            offeredMatches.Length == 1
                ? $"base='{offeredBaseName}' metadata='{offeredMatches[0].Metadata}' stack={offeredMatches[0].StackSize} size={offeredMatches[0].Width}x{offeredMatches[0].Height}"
                : "offered item missing");
        context.Check(offered.VendorOffer.Count > 0, "Nessa proposed proceeds",
            FormatItems(offered.VendorOffer));
        context.Observe("all sell-panel item widgets", FormatItems(offered.AllItems));
        context.Check(offeredSnapshot.Cursor.Action == CursorView.CursorAction.Free,
            "Ctrl+click did not leave held item", FormatCursor(offeredSnapshot.Cursor));
        context.Check(string.Equals(InventoryFingerprint(offeredSnapshot.Inventory), inventoryBefore, StringComparison.Ordinal),
            "underlying inventory remains unchanged while sale is staged",
            InventoryFingerprint(offeredSnapshot.Inventory));
        context.Observe("sell offer after entry", SellFingerprint(offered));
        if (offeredMatches.Length != 1 || offeredMatches[0].Rect is null || offered.VendorOffer.Count == 0)
            return await FailWithRecoveryAsync(context, target.Path, targetBaseName, inventoryBefore,
                sellBefore, "offered item or proposed proceeds were unreadable", "OfferOutcomeMismatch", cancellationToken);

        if (!await VerifySellHoverAsync(context, offeredMatches[0], targetBaseName,
                "Your Offer item", cancellationToken))
            return await FailWithRecoveryAsync(context, target.Path, targetBaseName, inventoryBefore,
                sellBefore, "offered source failed exact hover/tooltip identity", "TooltipMismatch", cancellationToken);

        foreach (var proceeds in ReadSell(context.Snapshot()).VendorOffer.ToArray())
        {
            if (!await VerifySellHoverAsync(context, proceeds, proceeds.BaseName,
                    $"Nessa proceeds '{proceeds.BaseName}'", cancellationToken))
                return await FailWithRecoveryAsync(context, target.Path, targetBaseName, inventoryBefore,
                    sellBefore, "proposed proceeds failed exact hover/tooltip identity", "TooltipMismatch", cancellationToken);
        }

        var liveOfferTarget = ReadSell(context.Snapshot()).PlayerOffer.Single(x =>
            string.Equals(x.Metadata, target.Path, StringComparison.Ordinal));
        if (liveOfferTarget.Rect is not { } offerRect)
            return await FailWithRecoveryAsync(context, target.Path, targetBaseName, inventoryBefore,
                sellBefore, "offered target geometry changed before withdrawal", "OfferOutcomeMismatch", cancellationToken);
        var withdrawPoint = context.Snapshot().Window.ToScreen(offerRect.CenterX, offerRect.CenterY);
        var withdraw = await context.VerifiedModifierClickAsync(
            withdrawPoint.X, withdrawPoint.Y, [LeftControlVk], ClickIntent.InteractUi,
            $"Ctrl+click sell offer -> inventory '{targetBaseName}'",
            () =>
            {
                var current = context.Snapshot();
                return current.Cursor.Action == CursorView.CursorAction.Free
                    && string.Equals(InventoryFingerprint(current.Inventory), inventoryBefore, StringComparison.Ordinal)
                    && string.Equals(SellFingerprint(ReadSell(current)), sellBefore, StringComparison.Ordinal);
            }, 4_000, cancellationToken);
        if (withdraw != ActionOutcome.Confirmed)
            return LiveTestCaseResult.Fail("offer withdrawal did not restore inventory and both offer regions", "RestoreFailed");
        if (!await context.WaitForInputIdleAsync("after sell-offer withdrawal", 1_500, cancellationToken))
            return LiveTestCaseResult.Fail("input did not settle after offer withdrawal", "InputSettleFailed");

        await MoveNeutralAsync(context, cancellationToken);
        var final = context.Snapshot();
        var finalSell = ReadSell(final);
        context.Check(finalSell.IsOpen, "sell window remains open", $"open={finalSell.IsOpen}");
        context.Check(final.Cursor.Action == CursorView.CursorAction.Free,
            "cursor restored free", FormatCursor(final.Cursor));
        context.Check(string.Equals(InventoryFingerprint(final.Inventory), inventoryBefore, StringComparison.Ordinal),
            "inventory fingerprint restored",
            $"before=[{inventoryBefore}] after=[{InventoryFingerprint(final.Inventory)}]");
        context.Check(string.Equals(SellFingerprint(finalSell), sellBefore, StringComparison.Ordinal),
            "empty sell offer restored",
            $"before=[{sellBefore}] after=[{SellFingerprint(finalSell)}]");
        if (!finalSell.IsOpen || final.Cursor.Action != CursorView.CursorAction.Free
            || !string.Equals(InventoryFingerprint(final.Inventory), inventoryBefore, StringComparison.Ordinal)
            || !string.Equals(SellFingerprint(finalSell), sellBefore, StringComparison.Ordinal))
            return LiveTestCaseResult.Fail("sell window, cursor, inventory, or offer did not exactly restore", "RestoreFailed");

        return LiveTestCaseResult.Pass(
            $"proved Ctrl+click staging/withdrawal for '{targetBaseName}', decoded and hovered Nessa's proceeds, never accepted, and restored the empty sell window with unchanged underlying inventory",
            "CompletedAndRestored");
    }

    private static async Task<LiveTestCaseResult> FailWithRecoveryAsync(
        LiveTestContext context,
        string targetPath,
        string targetBaseName,
        string inventoryBefore,
        string sellBefore,
        string failure,
        string classification,
        CancellationToken cancellationToken)
    {
        var sell = ReadSell(context.Snapshot());
        var matches = sell.PlayerOffer.Where(x =>
            string.Equals(x.Metadata, targetPath, StringComparison.Ordinal) && x.Rect is not null).ToArray();
        if (matches.Length == 1 && matches[0].Rect is { } rect)
        {
            await VerifySellHoverAsync(context, matches[0], targetBaseName,
                "recovery Your Offer item", cancellationToken);
            var point = context.Snapshot().Window.ToScreen(rect.CenterX, rect.CenterY);
            await context.VerifiedModifierClickAsync(
                point.X, point.Y, [LeftControlVk], ClickIntent.InteractUi,
                $"recovery Ctrl+click sell offer -> inventory '{targetBaseName}'",
                () =>
                {
                    var current = context.Snapshot();
                    return current.Cursor.Action == CursorView.CursorAction.Free
                        && string.Equals(InventoryFingerprint(current.Inventory), inventoryBefore, StringComparison.Ordinal)
                        && string.Equals(SellFingerprint(ReadSell(current)), sellBefore, StringComparison.Ordinal);
                }, 4_000, cancellationToken);
            await context.WaitForInputIdleAsync("after sell-offer recovery", 1_500, cancellationToken);
        }
        await MoveNeutralAsync(context, cancellationToken);
        var final = context.Snapshot();
        var restored = final.Cursor.Action == CursorView.CursorAction.Free
            && string.Equals(InventoryFingerprint(final.Inventory), inventoryBefore, StringComparison.Ordinal)
            && string.Equals(SellFingerprint(ReadSell(final)), sellBefore, StringComparison.Ordinal);
        context.Check(restored, "failure-path sell-offer restoration",
            restored ? "baseline restored" : "baseline mismatch");
        return LiveTestCaseResult.Fail(
            restored ? $"{failure}; starting state was restored" : $"{failure}; starting state was not restored",
            restored ? classification : "RestoreFailed");
    }

    private static async Task<bool> VerifyInventoryHoverAsync(
        LiveTestContext context,
        InventoryView.Item target,
        string baseName,
        string label,
        CancellationToken cancellationToken)
    {
        if (target.Rect is not { } rect) return false;
        var point = context.Snapshot().Window.ToScreen(rect.CenterX, rect.CenterY);
        await context.HoverAsync(point.X, point.Y, 120, cancellationToken);
        var reached = await context.WaitUntilAsync(label,
            () => ResolvesHoverTo(context.Snapshot(), target.ElementAddress), 1_500, cancellationToken, 20);
        var tooltip = ReadTooltip(context.Snapshot(), target.ElementAddress);
        context.Check(reached, $"{label} exact UIHover", $"metadata='{target.Path}' element=0x{(long)target.ElementAddress:X}");
        context.Check(tooltip.Root != 0 && tooltip.Lines.Any(x => x.Contains(baseName, StringComparison.OrdinalIgnoreCase)),
            $"{label} rendered tooltip", $"root=0x{(long)tooltip.Root:X} base='{baseName}' lines={tooltip.Lines.Count}");
        return reached && tooltip.Root != 0
            && tooltip.Lines.Any(x => x.Contains(baseName, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<bool> VerifySellHoverAsync(
        LiveTestContext context,
        SellWindowView.Item target,
        string baseName,
        string label,
        CancellationToken cancellationToken)
    {
        if (target.Rect is not { } rect) return false;
        var point = context.Snapshot().Window.ToScreen(rect.CenterX, rect.CenterY);
        await context.HoverAsync(point.X, point.Y, 120, cancellationToken);
        var reached = await context.WaitUntilAsync(label,
            () => ResolvesHoverTo(context.Snapshot(), target.Element), 1_500, cancellationToken, 20);
        var tooltip = ReadTooltip(context.Snapshot(), target.Element);
        context.Check(reached, $"{label} exact UIHover", $"metadata='{target.Metadata}' element=0x{(long)target.Element:X}");
        context.Check(tooltip.Root != 0 && tooltip.Lines.Any(x => x.Contains(baseName, StringComparison.OrdinalIgnoreCase)),
            $"{label} rendered tooltip", $"root=0x{(long)tooltip.Root:X} base='{baseName}' lines={tooltip.Lines.Count}");
        return reached && tooltip.Root != 0
            && tooltip.Lines.Any(x => x.Contains(baseName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ResolvesHoverTo(GameSnapshot snapshot, nint target)
    {
        snapshot.Reader.TryReadStruct<nint>(snapshot.IngameStateAddress + KnownOffsets.IngameState.UIHover, out var current);
        for (var depth = 0; depth < 24 && current != 0; depth++)
        {
            if (current == target) return true;
            if (!snapshot.Reader.TryReadStruct<nint>(current + KnownOffsets.Element.Parent, out var parent)
                || parent == current)
                break;
            current = parent;
        }
        return false;
    }

    private static (nint Root, IReadOnlyList<string> Lines) ReadTooltip(GameSnapshot snapshot, nint element)
    {
        snapshot.Reader.TryReadStruct<nint>(element + KnownOffsets.Element.RenderedTooltip, out var root);
        if (root == 0) return (0, []);
        var lines = new List<string>();
        var seen = new HashSet<nint>();
        var queue = new Queue<(nint Address, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0 && seen.Count < 1_024)
        {
            var (address, depth) = queue.Dequeue();
            if (!seen.Add(address)) continue;
            var node = ElementReader.TryReadSnapshot(snapshot.Reader, address, 128);
            if (node is null) continue;
            if (ElementReader.IsVisibleDeep(snapshot.Reader, address))
            {
                var text = NativeString.Read(snapshot.Reader, address + KnownOffsets.Element.TextNoTags);
                if (string.IsNullOrWhiteSpace(text))
                    text = NativeString.Read(snapshot.Reader, address + KnownOffsets.Element.Text);
                foreach (var line in text.Split(['\r', '\n'],
                             StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (!string.IsNullOrWhiteSpace(line)) lines.Add(line);
            }
            if (depth >= 14) continue;
            foreach (var child in node.Children) queue.Enqueue((child, depth + 1));
        }
        return (root, lines);
    }

    private static SellWindowView ReadSell(GameSnapshot snapshot)
        => SellWindowView.Read(snapshot.Reader, snapshot.IngameStateAddress);

    private static string ReadBaseName(MemoryReader reader, nint itemEntity)
    {
        var components = EntityComponents.ReadComponentMap(reader, itemEntity);
        if (!components.TryGetValue("Base", out var component)
            || !reader.TryReadStruct<nint>(component + KnownOffsets.BaseComponent.ItemInfo, out var info)
            || info == 0)
            return string.Empty;
        return NativeString.Read(reader, info + KnownOffsets.ItemInfo.BaseName);
    }

    private static string InventoryFingerprint(InventoryView inventory)
        => string.Join('|', inventory.Items
            .OrderBy(x => x.Path, StringComparer.Ordinal)
            .ThenBy(x => x.Rect?.Y ?? float.MaxValue).ThenBy(x => x.Rect?.X ?? float.MaxValue)
            .Select(x => $"{x.Path}:{x.StackSize}:{x.Width}x{x.Height}:{FormatRect(x.Rect)}"));

    private static string SellFingerprint(SellWindowView sell)
        => $"player=[{ItemFingerprint(sell.PlayerOffer)}];vendor=[{ItemFingerprint(sell.VendorOffer)}]";

    private static string ItemFingerprint(IEnumerable<SellWindowView.Item> items)
        => string.Join('|', items.OrderBy(x => x.Metadata, StringComparer.Ordinal)
            .ThenBy(x => x.Rect?.Y ?? float.MaxValue).ThenBy(x => x.Rect?.X ?? float.MaxValue)
            .Select(x => $"{x.Metadata}:{x.BaseName}:{x.StackSize}:{x.Width}x{x.Height}:{FormatRect(x.Rect)}"));

    private static string FormatItems(IEnumerable<SellWindowView.Item> items)
    {
        var values = items.Select(x => $"{x.BaseName}[{x.Metadata}] stack={x.StackSize} size={x.Width}x{x.Height}").ToArray();
        return values.Length == 0 ? "empty" : string.Join(" | ", values);
    }

    private static string FormatRect(ElementGeometry.Rect? rect)
        => rect is { } r ? $"{r.X:F0},{r.Y:F0},{r.Width:F0},{r.Height:F0}" : "none";

    private static string FormatCursor(CursorView cursor)
        => $"readable={cursor.IsReadable} address=0x{(long)cursor.Address:X} action={cursor.Action}";

    private static async Task MoveNeutralAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        var window = context.Snapshot().Window;
        await context.HoverAsync(window.OriginX + window.Width / 2, window.OriginY + 80, 150, cancellationToken);
    }
}
