using BubblesBot.Bot.Input;
using BubblesBot.Core;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Researches and proves normal-click pickup plus exact-cell placement restoration.</summary>
public sealed class CursorPickupRoundTripLiveTest : ILiveTestCase
{
    public string Id => "H-06-cursor-pickup-roundtrip";
    public string Name => "Cursor item pickup/placement round trip";
    public string Description => "Normal-clicks one unique equipment item, proves an exact source-only delta plus Cursor.HoldItem, then places it back into its original cell and proves Cursor.Free and full restoration.";
    public string ManualSetup => "Open an empty normal stash tab and inventory, carry a uniquely pathed non-stackable weapon or armour item, and ensure the cursor is not holding an item.";
    public LiveTestMutation Mutation => LiveTestMutation.Reversible;
    public bool DrivesInput => true;

    public async Task<LiveTestCaseResult> RunAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        var baseline = context.Snapshot();
        var inventory = baseline.Inventory;
        var stash = baseline.StashInventory;
        var cursorBefore = baseline.Cursor;
        context.Check(inventory.IsOpen && stash.IsOpen, "prepared panels",
            $"inventory={inventory.IsOpen} stash={stash.IsOpen}");
        context.Check(stash.Items.Count == 0, "prepared empty stash", $"tab={stash.VisibleTabIndex} items={stash.Items.Count}");
        context.Check(cursorBefore.IsReadable, "cursor root", $"address=0x{(long)cursorBefore.Address:X}");
        context.Check(cursorBefore.Action == CursorView.CursorAction.Free,
            "cursor initially free", FormatCursor(cursorBefore));
        if (!inventory.IsOpen || !stash.IsOpen || stash.Items.Count != 0
            || !cursorBefore.IsReadable || cursorBefore.Action != CursorView.CursorAction.Free)
            return LiveTestCaseResult.Blocked("requires open inventory/empty stash and a readable free cursor", "PreparedStateMismatch");

        var candidates = inventory.Items
            .Where(x => x.StackSize == 1 && x.Rect is { Width: > 0, Height: > 0 }
                && (x.Path.StartsWith("Metadata/Items/Weapons/", StringComparison.Ordinal)
                    || x.Path.StartsWith("Metadata/Items/Armours/", StringComparison.Ordinal)))
            .Where(x => inventory.Items.Count(y => string.Equals(y.Path, x.Path, StringComparison.Ordinal)) == 1)
            .OrderBy(x => x.OccupiedCells).ThenBy(x => x.Path, StringComparer.Ordinal).ToArray();
        context.Check(candidates.Length > 0, "unique pickup candidate",
            candidates.Length == 0 ? "none" : string.Join(", ", candidates.Select(x => x.Path)));
        if (candidates.Length == 0 || candidates[0].Rect is not { } sourceRect)
            return LiveTestCaseResult.Blocked("no safe unique equipment pickup candidate", "NoSafePickupCandidate");

        var target = candidates[0];
        var baseName = ReadBaseName(baseline.Reader, target.ItemEntity);
        var inventoryBefore = Fingerprint(inventory);
        var expectedHeldInventory = Fingerprint(inventory, target.Path);
        var stashBefore = StashFingerprint(stash);
        context.Check(!string.IsNullOrWhiteSpace(baseName), "pickup target identity",
            $"base='{baseName}' metadata='{target.Path}' rect={sourceRect}");
        if (string.IsNullOrWhiteSpace(baseName))
            return LiveTestCaseResult.Blocked("pickup target base name is unreadable", "CandidateUnreadable");

        if (!await VerifyHoverAsync(context, target, baseName, cancellationToken))
            return LiveTestCaseResult.Fail("pickup target failed exact hover/tooltip identity", "TooltipMismatch");

        var sourcePoint = context.Snapshot().Window.ToScreen(sourceRect.CenterX, sourceRect.CenterY);
        var pickup = await context.VerifiedClickAsync(
            sourcePoint.X, sourcePoint.Y, ClickIntent.InteractUi,
            $"normal-click pick up '{baseName}'",
            () =>
            {
                var current = context.Snapshot();
                return current.Cursor.Action == CursorView.CursorAction.HoldItem
                    && string.Equals(Fingerprint(current.Inventory), expectedHeldInventory, StringComparison.Ordinal)
                    && string.Equals(StashFingerprint(current.StashInventory), stashBefore, StringComparison.Ordinal);
            }, 3_000, cancellationToken);
        if (!await context.WaitForInputIdleAsync("after normal-click pickup", 1_500, cancellationToken))
            return await FailAfterPlacementRecoveryAsync(context, sourcePoint, inventoryBefore, stashBefore,
                "input did not settle after pickup", "InputSettleFailed", cancellationToken);

        var held = context.Snapshot();
        var cursorHeld = held.Cursor;
        context.Observe("cursor transition", $"before=[{FormatCursor(cursorBefore)}] held=[{FormatCursor(cursorHeld)}]");
        context.Check(pickup == ActionOutcome.Confirmed, "pickup outcome", $"outcome={pickup}");
        context.Check(cursorHeld.Action == CursorView.CursorAction.HoldItem,
            "cursor holds an item", FormatCursor(cursorHeld));
        context.Check(string.Equals(Fingerprint(held.Inventory), expectedHeldInventory, StringComparison.Ordinal),
            "exact source-only inventory delta",
            $"expected=[{expectedHeldInventory}] actual=[{Fingerprint(held.Inventory)}]");
        context.Check(string.Equals(StashFingerprint(held.StashInventory), stashBefore, StringComparison.Ordinal),
            "stash unchanged while item held", StashFingerprint(held.StashInventory));
        context.Check(!held.Inventory.Items.Any(x => string.Equals(x.Path, target.Path, StringComparison.Ordinal)),
            "held identity resolves from unique removed source",
            $"base='{baseName}' metadata='{target.Path}' is the sole baseline item absent while Cursor.Action=HoldItem");
        if (pickup != ActionOutcome.Confirmed || cursorHeld.Action != CursorView.CursorAction.HoldItem
            || !string.Equals(Fingerprint(held.Inventory), expectedHeldInventory, StringComparison.Ordinal)
            || !string.Equals(StashFingerprint(held.StashInventory), stashBefore, StringComparison.Ordinal))
            return await FailAfterPlacementRecoveryAsync(context, sourcePoint, inventoryBefore, stashBefore,
                "normal-click pickup did not prove exact held-item identity", "HeldItemIdentityMismatch", cancellationToken);

        var place = await context.VerifiedClickAsync(
            sourcePoint.X, sourcePoint.Y, ClickIntent.InteractUi,
            $"place held '{baseName}' into original inventory cell",
            () =>
            {
                var current = context.Snapshot();
                return current.Cursor.Action == CursorView.CursorAction.Free
                    && string.Equals(Fingerprint(current.Inventory), inventoryBefore, StringComparison.Ordinal)
                    && string.Equals(StashFingerprint(current.StashInventory), stashBefore, StringComparison.Ordinal);
            }, 3_000, cancellationToken);
        if (place != ActionOutcome.Confirmed)
            return LiveTestCaseResult.Fail("original-cell placement did not restore cursor and containers", "RestoreFailed");
        if (!await context.WaitForInputIdleAsync("after original-cell placement", 1_500, cancellationToken))
            return LiveTestCaseResult.Fail("input did not settle after placement", "InputSettleFailed");

        await MoveNeutralAsync(context, cancellationToken);
        var final = context.Snapshot();
        var cursorAfter = final.Cursor;
        context.Check(cursorAfter.Action == CursorView.CursorAction.Free,
            "cursor restored free", FormatCursor(cursorAfter));
        context.Check(string.Equals(Fingerprint(final.Inventory), inventoryBefore, StringComparison.Ordinal),
            "inventory fingerprint restored", $"before=[{inventoryBefore}] after=[{Fingerprint(final.Inventory)}]");
        context.Check(string.Equals(StashFingerprint(final.StashInventory), stashBefore, StringComparison.Ordinal),
            "stash fingerprint restored", $"before=[{stashBefore}] after=[{StashFingerprint(final.StashInventory)}]");
        if (cursorAfter.Action != CursorView.CursorAction.Free
            || !string.Equals(Fingerprint(final.Inventory), inventoryBefore, StringComparison.Ordinal)
            || !string.Equals(StashFingerprint(final.StashInventory), stashBefore, StringComparison.Ordinal))
            return LiveTestCaseResult.Fail("cursor or container baseline was not restored", "RestoreFailed");

        return LiveTestCaseResult.Pass(
            $"proved normal-click pickup identity for '{baseName}' through Cursor.HoldItem plus the unique exact source delta, then restored its original cell and Cursor.Free",
            "CompletedAndRestored");
    }

    private static async Task<LiveTestCaseResult> FailAfterPlacementRecoveryAsync(
        LiveTestContext context,
        (int X, int Y) sourcePoint,
        string inventoryBefore,
        string stashBefore,
        string failure,
        string classification,
        CancellationToken cancellationToken)
    {
        var cursor = context.Snapshot().Cursor;
        if (cursor.Action == CursorView.CursorAction.HoldItem)
        {
            await context.VerifiedClickAsync(
                sourcePoint.X, sourcePoint.Y, ClickIntent.InteractUi,
                "recovery place held item into original inventory cell",
                () =>
                {
                    var recovered = context.Snapshot();
                    return recovered.Cursor.Action == CursorView.CursorAction.Free
                        && string.Equals(Fingerprint(recovered.Inventory), inventoryBefore, StringComparison.Ordinal)
                        && string.Equals(StashFingerprint(recovered.StashInventory), stashBefore, StringComparison.Ordinal);
                }, 3_000, cancellationToken);
            await context.WaitForInputIdleAsync("after pickup recovery", 1_500, cancellationToken);
        }
        await MoveNeutralAsync(context, cancellationToken);
        var final = context.Snapshot();
        var restored = final.Cursor.Action == CursorView.CursorAction.Free
            && string.Equals(Fingerprint(final.Inventory), inventoryBefore, StringComparison.Ordinal)
            && string.Equals(StashFingerprint(final.StashInventory), stashBefore, StringComparison.Ordinal);
        context.Check(restored, "failure-path pickup restoration", restored ? "baseline restored" : "baseline mismatch");
        return LiveTestCaseResult.Fail(
            restored ? $"{failure}; starting state was restored" : $"{failure}; starting state was not restored",
            restored ? classification : "RestoreFailed");
    }

    private static async Task<bool> VerifyHoverAsync(
        LiveTestContext context,
        InventoryView.Item target,
        string baseName,
        CancellationToken cancellationToken)
    {
        if (target.Rect is not { } rect) return false;
        var point = context.Snapshot().Window.ToScreen(rect.CenterX, rect.CenterY);
        await context.HoverAsync(point.X, point.Y, 120, cancellationToken);
        var reached = await context.WaitUntilAsync("pickup target hover",
            () => ResolvesHoverTo(context.Snapshot(), target.ElementAddress), 1_500, cancellationToken, 20);
        var snapshot = context.Snapshot();
        snapshot.Reader.TryReadStruct<nint>(target.ElementAddress + KnownOffsets.Element.RenderedTooltip, out var tooltip);
        context.Check(reached, "pickup target exact UIHover",
            $"element=0x{(long)target.ElementAddress:X} metadata='{target.Path}'");
        context.Check(tooltip != 0, "pickup target rendered tooltip",
            $"root=0x{(long)tooltip:X} base='{baseName}'");
        return reached && tooltip != 0;
    }

    private static bool ResolvesHoverTo(GameSnapshot snapshot, nint target)
    {
        snapshot.Reader.TryReadStruct<nint>(
            snapshot.IngameStateAddress + KnownOffsets.IngameState.UIHover, out var current);
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

    private static string FormatCursor(CursorView cursor)
        => $"readable={cursor.IsReadable} root=0x{(long)cursor.Address:X} action={cursor.Action}({(byte)cursor.Action})";

    private static string ReadBaseName(MemoryReader reader, nint itemEntity)
    {
        var components = EntityComponents.ReadComponentMap(reader, itemEntity);
        if (!components.TryGetValue("Base", out var component)
            || !reader.TryReadStruct<nint>(component + KnownOffsets.BaseComponent.ItemInfo, out var info)
            || info == 0)
            return string.Empty;
        return NativeString.Read(reader, info + KnownOffsets.ItemInfo.BaseName);
    }

    private static string Fingerprint(InventoryView inventory, string? excludeUniquePath = null)
        => string.Join('|', inventory.Items
            .Where(x => excludeUniquePath is null || !string.Equals(x.Path, excludeUniquePath, StringComparison.Ordinal))
            .OrderBy(x => x.Path, StringComparer.Ordinal)
            .ThenBy(x => x.Rect?.Y ?? float.MaxValue).ThenBy(x => x.Rect?.X ?? float.MaxValue)
            .Select(x => $"{x.Path}:{x.StackSize}:{x.Width}x{x.Height}:{FormatRect(x.Rect)}"));

    private static string StashFingerprint(StashInventoryView stash)
        => string.Join('|', stash.Items.OrderBy(x => x.Path, StringComparer.Ordinal)
            .ThenBy(x => x.Rect?.Y ?? float.MaxValue).ThenBy(x => x.Rect?.X ?? float.MaxValue)
            .Select(x => $"{x.Path}:{x.StackSize}:{x.Width}x{x.Height}:{FormatRect(x.Rect)}"));

    private static string FormatRect(ElementGeometry.Rect? rect)
        => rect is { } r ? $"{r.X:F0},{r.Y:F0},{r.Width:F0},{r.Height:F0}" : "none";

    private static async Task MoveNeutralAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        var window = context.Snapshot().Window;
        await context.HoverAsync(window.OriginX + window.Width / 2, window.OriginY + 80, 150, cancellationToken);
    }
}
