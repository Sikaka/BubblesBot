using BubblesBot.Bot.Input;
using BubblesBot.Bot.Systems;
using BubblesBot.Core;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Stages one sale, clicks semantic Cancel, classifies continuation, and reopens Sell.</summary>
public sealed class SellCancelRoundTripLiveTest : ILiveTestCase
{
    private const int LeftControlVk = 0xA2;
    private const string NessaPath = "Metadata/NPC/Act1/Nessa";
    private const string SellOption = "Sell Items";
    private static readonly IReadOnlySet<string> Allowed =
        new HashSet<string>(StringComparer.Ordinal) { "SellWindow", "NpcDialog" };

    public string Id => "A-04-sell-cancel-roundtrip";
    public string Name => "Staged sale Cancel round trip";
    public string Description => "Stages one exact item and decoded proceeds, clicks the exact Cancel control without accepting, classifies the continuation, reopens Sell Items, and restores the starting state.";
    public string ManualSetup => "Open Nessa's Sell Items window with both offer regions empty. Carry a unique non-stackable equipment item and hold nothing on the cursor.";
    public LiveTestMutation Mutation => LiveTestMutation.Reversible;
    public bool DrivesInput => true;
    public IReadOnlySet<string> AllowedBlockingPanels => Allowed;

    public async Task<LiveTestCaseResult> RunAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        var baseline = context.Snapshot();
        var sell = ReadSell(baseline);
        if (!sell.IsOpen && ReadDialog(baseline).IsOpen)
        {
            context.Observe("prepared-state recovery",
                "prior Cancel left the expected Nessa dialog continuation; reopening Sell Items before the measured cycle");
            if (!await OpenSellAsync(context, cancellationToken))
                return LiveTestCaseResult.Fail("could not recover the prepared empty Sell Items window", "SetupRecoveryFailed");
            baseline = context.Snapshot();
            sell = ReadSell(baseline);
        }
        var inventory = baseline.Inventory;
        var inventoryBefore = InventoryFingerprint(inventory);
        context.Check(sell.IsOpen && inventory.IsOpen, "prepared sell/inventory panels",
            $"sell={sell.IsOpen} inventory={inventory.IsOpen}");
        context.Check(sell.PlayerOffer.Count == 0 && sell.VendorOffer.Count == 0,
            "prepared empty offer", $"player={sell.PlayerOffer.Count} vendor={sell.VendorOffer.Count}");
        context.Check(sell.Cancel?.Rect is not null, "semantic Cancel control", FormatControl(sell.Cancel));
        context.Check(baseline.Cursor.Action == CursorView.CursorAction.Free,
            "prepared free cursor", baseline.Cursor.Action.ToString());
        if (!sell.IsOpen || !inventory.IsOpen || sell.PlayerOffer.Count != 0
            || sell.VendorOffer.Count != 0 || sell.Cancel?.Rect is null
            || baseline.Cursor.Action != CursorView.CursorAction.Free)
            return LiveTestCaseResult.Blocked("requires an empty readable Nessa sell window and free cursor", "PreparedStateMismatch");

        var candidates = inventory.Items
            .Where(x => x.StackSize == 1 && x.Rect is { Width: > 0, Height: > 0 }
                && (x.Path.StartsWith("Metadata/Items/Weapons/", StringComparison.Ordinal)
                    || x.Path.StartsWith("Metadata/Items/Armours/", StringComparison.Ordinal)))
            .Where(x => inventory.Items.Count(y => string.Equals(y.Path, x.Path, StringComparison.Ordinal)) == 1)
            .OrderBy(x => x.OccupiedCells).ThenBy(x => x.Path, StringComparer.Ordinal).ToArray();
        if (candidates.Length == 0 || candidates[0].Rect is not { } targetRect)
            return LiveTestCaseResult.Blocked("no safe unique equipment sale candidate", "NoSafeOfferCandidate");
        var target = candidates[0];
        var baseName = ReadBaseName(baseline.Reader, target.ItemEntity);
        context.Check(!string.IsNullOrWhiteSpace(baseName), "sale candidate identity",
            $"base='{baseName}' metadata='{target.Path}'");
        if (string.IsNullOrWhiteSpace(baseName))
            return LiveTestCaseResult.Blocked("sale candidate identity unreadable", "CandidateUnreadable");

        if (!await VerifyHoverAsync(context, target.ElementAddress, targetRect, target.Path,
                baseName, "inventory sale source", cancellationToken))
            return LiveTestCaseResult.Fail("sale source failed exact hover/tooltip identity", "TooltipMismatch");
        var sourcePoint = context.Snapshot().Window.ToScreen(targetRect.CenterX, targetRect.CenterY);
        var stage = await context.VerifiedModifierClickAsync(
            sourcePoint.X, sourcePoint.Y, [LeftControlVk], ClickIntent.InteractUi,
            $"Ctrl+click stage sale '{baseName}'",
            () =>
            {
                var current = context.Snapshot();
                var currentSell = ReadSell(current);
                return currentSell.PlayerOffer.Count(x => string.Equals(x.Metadata, target.Path, StringComparison.Ordinal)) == 1
                    && currentSell.VendorOffer.Count > 0
                    && current.Cursor.Action == CursorView.CursorAction.Free
                    && string.Equals(InventoryFingerprint(current.Inventory), inventoryBefore, StringComparison.Ordinal);
            }, 4_000, cancellationToken);
        if (stage != ActionOutcome.Confirmed)
            return LiveTestCaseResult.Fail("could not stage exact reversible sale", "OfferOutcomeMismatch");
        if (!await context.WaitForInputIdleAsync("after sale staging", 1_500, cancellationToken))
            return LiveTestCaseResult.Fail("input did not settle after sale staging", "InputSettleFailed");

        var stagedSnapshot = context.Snapshot();
        var staged = ReadSell(stagedSnapshot);
        context.Check(staged.PlayerOffer.Count == 1
                && string.Equals(staged.PlayerOffer[0].Metadata, target.Path, StringComparison.Ordinal)
                && string.Equals(staged.PlayerOffer[0].BaseName, baseName, StringComparison.Ordinal),
            "exact staged item", staged.PlayerOffer.Count == 1
                ? $"{staged.PlayerOffer[0].BaseName}[{staged.PlayerOffer[0].Metadata}]"
                : $"items={staged.PlayerOffer.Count}");
        context.Check(staged.VendorOffer.Count > 0, "decoded proposed proceeds", FormatItems(staged.VendorOffer));
        if (staged.PlayerOffer.Count != 1 || staged.VendorOffer.Count == 0)
            return await RecoverStagedAsync(context, target.Path, inventoryBefore,
                "staged sale became unreadable", "OfferOutcomeMismatch", cancellationToken);

        var cancel = ReadSell(context.Snapshot()).Cancel;
        if (cancel?.Rect is not { } cancelRect)
            return await RecoverStagedAsync(context, target.Path, inventoryBefore,
                "Cancel control disappeared after staging", "CancelUnreadable", cancellationToken);
        var cancelPoint = context.Snapshot().Window.ToScreen(cancelRect.CenterX, cancelRect.CenterY);
        await context.HoverAsync(cancelPoint.X, cancelPoint.Y, 120, cancellationToken);
        var hoverSnapshot = context.Snapshot();
        hoverSnapshot.Reader.TryReadStruct<nint>(
            hoverSnapshot.IngameStateAddress + KnownOffsets.IngameState.UIHover, out var rawHover);
        context.Observe("Cancel UIHover capability",
            $"UIHover=0x{(long)rawHover:X} resolvesToControl={ResolvesHoverTo(hoverSnapshot, cancel.Element)}; ordinary button validation uses semantic text/rect stability");
        var liveSell = ReadSell(hoverSnapshot);
        var liveCancel = liveSell.Cancel;
        var liveAccept = liveSell.Accept;
        context.Check(liveCancel is not null
                && liveCancel.Element == cancel.Element
                && string.Equals(liveCancel.Text, "cancel", StringComparison.OrdinalIgnoreCase)
                && liveCancel.Rect == cancel.Rect,
            "final Cancel identity",
            liveCancel is null ? "missing" : $"text='{liveCancel.Text}' element=0x{(long)liveCancel.Element:X}");
        context.Check(liveCancel?.Rect is { } semanticCancelRect
                && semanticCancelRect.Contains(cancelRect.CenterX, cancelRect.CenterY)
                && (liveAccept?.Rect is not { } acceptRect
                    || !acceptRect.Contains(cancelRect.CenterX, cancelRect.CenterY)),
            "Cancel point excludes Accept",
            $"point={cancelRect.CenterX:F0},{cancelRect.CenterY:F0} cancel={liveCancel?.Rect} accept={liveAccept?.Rect}");
        if (liveCancel?.Rect is not { } liveCancelRect
            || liveCancel.Element != cancel.Element
            || !string.Equals(liveCancel.Text, "cancel", StringComparison.OrdinalIgnoreCase)
            || liveCancelRect != cancel.Rect
            || (liveAccept?.Rect is { } liveAcceptRect
                && liveAcceptRect.Contains(cancelRect.CenterX, cancelRect.CenterY)))
            return await RecoverStagedAsync(context, target.Path, inventoryBefore,
                "Cancel semantic identity or exclusion failed", "CancelUnreadable", cancellationToken);

        var livePoint = context.Snapshot().Window.ToScreen(liveCancelRect.CenterX, liveCancelRect.CenterY);
        var cancelled = await context.VerifiedClickAsync(
            livePoint.X, livePoint.Y, ClickIntent.InteractUi,
            "click exact staged-sale Cancel",
            () =>
            {
                var current = context.Snapshot();
                return !ReadSell(current).IsOpen
                    && ReadDialog(current).IsOpen
                    && !current.Inventory.IsOpen
                    && current.Cursor.Action == CursorView.CursorAction.Free;
            },
            4_000, cancellationToken);
        if (cancelled != ActionOutcome.Confirmed)
            return LiveTestCaseResult.Fail("Cancel did not close Sell/Inventory and return to Nessa's dialog", "CancelOutcomeMismatch");
        if (!await context.WaitForInputIdleAsync("after staged-sale Cancel", 1_500, cancellationToken))
            return LiveTestCaseResult.Fail("input did not settle after Cancel", "InputSettleFailed");

        var afterCancel = context.Snapshot();
        var dialog = ReadDialog(afterCancel);
        context.Observe("Cancel continuation",
            $"openPanels=[{string.Join(", ", afterCancel.OpenPanels.Open)}] npcDialog={dialog.IsOpen}");
        context.Check(dialog.IsOpen && !afterCancel.Inventory.IsOpen,
            "Cancel panel continuation",
            $"npcDialog={dialog.IsOpen} inventory={afterCancel.Inventory.IsOpen}");
        if (!await OpenSellAsync(context, cancellationToken))
            return LiveTestCaseResult.Fail("Cancel succeeded but Nessa Sell Items could not be reopened", "RestoreFailed");

        var final = context.Snapshot();
        var finalSell = ReadSell(final);
        context.Check(finalSell.IsOpen && finalSell.PlayerOffer.Count == 0 && finalSell.VendorOffer.Count == 0,
            "empty sell window restored",
            $"open={finalSell.IsOpen} player={finalSell.PlayerOffer.Count} vendor={finalSell.VendorOffer.Count}");
        context.Check(final.Cursor.Action == CursorView.CursorAction.Free, "cursor restored free", final.Cursor.Action.ToString());
        context.Check(string.Equals(InventoryFingerprint(final.Inventory), inventoryBefore, StringComparison.Ordinal),
            "inventory fingerprint restored", InventoryFingerprint(final.Inventory));
        if (!finalSell.IsOpen || finalSell.PlayerOffer.Count != 0 || finalSell.VendorOffer.Count != 0
            || final.Cursor.Action != CursorView.CursorAction.Free
            || !string.Equals(InventoryFingerprint(final.Inventory), inventoryBefore, StringComparison.Ordinal))
            return LiveTestCaseResult.Fail("starting Nessa sell setup did not exactly restore", "RestoreFailed");

        return LiveTestCaseResult.Pass(
            $"staged exact '{baseName}', clicked semantic Cancel without accepting, classified continuation, and restored the empty Nessa sell window and inventory",
            "CompletedAndRestored");
    }

    private static async Task<bool> OpenSellAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        var dialog = ReadDialog(context.Snapshot());
        if (!dialog.IsOpen)
        {
            var snapshot = context.Snapshot();
            var nessa = snapshot.GroundLabels.SingleOrDefault(x => x.Path == NessaPath
                && string.Equals(x.RenderName, "Nessa", StringComparison.Ordinal)
                && x.IsLabelVisible && x.IsRectOnScreen);
            if (nessa?.LabelRect is not { } rect) return false;
            var occluders = snapshot.GroundLabels.Where(x => x.LabelAddress != nessa.LabelAddress && x.IsLabelVisible)
                .Select(x => x.LabelRect).Where(x => x is not null).Select(x => x!.Value).ToArray();
            var uncovered = InteractSystem.FindUncoveredPoint(rect, occluders);
            if (uncovered is not { } point) return false;
            var screen = snapshot.Window.ToScreen(point.X, point.Y);
            var openNpc = await context.VerifiedClickAsync(
                screen.X, screen.Y, ClickIntent.InteractWorld, "open exact Nessa dialog after Cancel",
                () => ReadDialog(context.Snapshot()).IsOpen, 3_000, cancellationToken);
            if (openNpc != ActionOutcome.Confirmed) return false;
            if (!await context.WaitForInputIdleAsync("after Nessa reopen", 1_500, cancellationToken)) return false;
            dialog = ReadDialog(context.Snapshot());
        }

        var matches = dialog.FindExact(SellOption).Where(x => x.Rect is { Width: > 0, Height: > 0 }).ToArray();
        context.Check(matches.Length == 1, "Sell Items continuation option", $"matches={matches.Length}");
        if (matches.Length != 1 || matches[0].Rect is not { } rect2) return false;
        var point2 = context.Snapshot().Window.ToScreen(rect2.CenterX, rect2.CenterY);
        var openSell = await context.VerifiedClickAsync(
            point2.X, point2.Y, ClickIntent.InteractUi, "reopen exact Nessa Sell Items",
            () => ReadSell(context.Snapshot()).IsOpen, 3_000, cancellationToken);
        if (openSell != ActionOutcome.Confirmed) return false;
        return await context.WaitForInputIdleAsync("after Sell Items reopen", 1_500, cancellationToken);
    }

    private static async Task<LiveTestCaseResult> RecoverStagedAsync(
        LiveTestContext context,
        string path,
        string inventoryBefore,
        string failure,
        string classification,
        CancellationToken cancellationToken)
    {
        var matches = ReadSell(context.Snapshot()).PlayerOffer
            .Where(x => string.Equals(x.Metadata, path, StringComparison.Ordinal) && x.Rect is not null).ToArray();
        if (matches.Length == 1 && matches[0].Rect is { } rect)
        {
            var point = context.Snapshot().Window.ToScreen(rect.CenterX, rect.CenterY);
            await context.VerifiedModifierClickAsync(
                point.X, point.Y, [LeftControlVk], ClickIntent.InteractUi,
                "recovery withdraw staged sale",
                () =>
                {
                    var current = context.Snapshot();
                    var sell = ReadSell(current);
                    return sell.IsOpen && sell.PlayerOffer.Count == 0 && sell.VendorOffer.Count == 0
                        && string.Equals(InventoryFingerprint(current.Inventory), inventoryBefore, StringComparison.Ordinal);
                }, 4_000, cancellationToken);
            await context.WaitForInputIdleAsync("after staged-sale recovery", 1_500, cancellationToken);
        }
        var final = context.Snapshot();
        var restored = ReadSell(final).IsOpen
            && ReadSell(final).PlayerOffer.Count == 0 && ReadSell(final).VendorOffer.Count == 0
            && string.Equals(InventoryFingerprint(final.Inventory), inventoryBefore, StringComparison.Ordinal);
        context.Check(restored, "failure-path Cancel-test restoration", restored ? "baseline restored" : "baseline mismatch");
        return LiveTestCaseResult.Fail(restored ? $"{failure}; baseline restored" : $"{failure}; baseline not restored",
            restored ? classification : "RestoreFailed");
    }

    private static async Task<bool> VerifyHoverAsync(
        LiveTestContext context,
        nint element,
        ElementGeometry.Rect rect,
        string metadata,
        string baseName,
        string label,
        CancellationToken cancellationToken)
    {
        var point = context.Snapshot().Window.ToScreen(rect.CenterX, rect.CenterY);
        await context.HoverAsync(point.X, point.Y, 120, cancellationToken);
        var reached = await context.WaitUntilAsync(label,
            () => ResolvesHoverTo(context.Snapshot(), element), 1_500, cancellationToken, 20);
        var snapshot = context.Snapshot();
        snapshot.Reader.TryReadStruct<nint>(element + KnownOffsets.Element.RenderedTooltip, out var tooltip);
        context.Check(reached, $"{label} exact UIHover", $"metadata='{metadata}' element=0x{(long)element:X}");
        context.Check(tooltip != 0, $"{label} rendered tooltip", $"root=0x{(long)tooltip:X} base='{baseName}'");
        return reached && tooltip != 0;
    }

    private static bool ResolvesHoverTo(GameSnapshot snapshot, nint target)
    {
        snapshot.Reader.TryReadStruct<nint>(snapshot.IngameStateAddress + KnownOffsets.IngameState.UIHover, out var current);
        for (var depth = 0; depth < 24 && current != 0; depth++)
        {
            if (current == target) return true;
            if (!snapshot.Reader.TryReadStruct<nint>(current + KnownOffsets.Element.Parent, out var parent)
                || parent == current) break;
            current = parent;
        }
        return false;
    }

    private static SellWindowView ReadSell(GameSnapshot snapshot)
        => SellWindowView.Read(snapshot.Reader, snapshot.IngameStateAddress);

    private static NpcDialogView ReadDialog(GameSnapshot snapshot)
        => NpcDialogView.Read(snapshot.Reader, snapshot.IngameStateAddress);

    private static string ReadBaseName(MemoryReader reader, nint entity)
    {
        var components = EntityComponents.ReadComponentMap(reader, entity);
        if (!components.TryGetValue("Base", out var component)
            || !reader.TryReadStruct<nint>(component + KnownOffsets.BaseComponent.ItemInfo, out var info)
            || info == 0) return string.Empty;
        return NativeString.Read(reader, info + KnownOffsets.ItemInfo.BaseName);
    }

    private static string InventoryFingerprint(InventoryView inventory)
        => string.Join('|', inventory.Items.OrderBy(x => x.Path, StringComparer.Ordinal)
            .ThenBy(x => x.Rect?.Y ?? float.MaxValue).ThenBy(x => x.Rect?.X ?? float.MaxValue)
            .Select(x => $"{x.Path}:{x.StackSize}:{x.Width}x{x.Height}:{FormatRect(x.Rect)}"));

    private static string FormatItems(IEnumerable<SellWindowView.Item> items)
        => string.Join(" | ", items.Select(x => $"{x.BaseName}[{x.Metadata}] stack={x.StackSize}"));

    private static string FormatControl(SellWindowView.Control? control)
        => control is null ? "missing" : $"text='{control.Text}' element=0x{(long)control.Element:X} rect={control.Rect}";

    private static string FormatRect(ElementGeometry.Rect? rect)
        => rect is { } r ? $"{r.X:F0},{r.Y:F0},{r.Width:F0},{r.Height:F0}" : "none";
}
