using BubblesBot.Bot.Input;
using BubblesBot.Core;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Reversible inventory -> active main hand -> inventory equipment interaction.</summary>
public sealed class EquipmentWeaponRoundTripLiveTest : ILiveTestCase
{
    private const int MainHandUiIndex = 9;
    private const int OffHandUiIndex = 10;
    private const int MainHandServerType = 2;
    private const int MainHandServerSlot = 2;
    private const int OffHandServerType = 3;
    private const int OffHandServerSlot = 3;

    public string Id => "A-06-equip-weapon-roundtrip";
    public string Name => "Active main-hand equip/unequip round trip";
    public string Description => "Normal-clicks one inventory weapon onto the empty active main hand, proves cursor/container/server/tooltip identity, then restores the exact inventory layout.";
    public string ManualSetup => "Open inventory on weapon set 1, leave its main hand and off hand empty, carry at least one non-stackable weapon, leave cursor free, and keep PoE focused.";
    public LiveTestMutation Mutation => LiveTestMutation.Reversible;
    public bool DrivesInput => true;

    public async Task<LiveTestCaseResult> RunAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        var baseline = context.Snapshot();
        var layout = EquipmentInventoriesView.From(baseline);
        var main = Ui(layout, MainHandUiIndex);
        var off = Ui(layout, OffHandUiIndex);
        var serverMain = Server(layout, MainHandServerType, MainHandServerSlot);
        var serverOff = Server(layout, OffHandServerType, OffHandServerSlot);
        var prepared = baseline.Inventory.IsOpen
            && baseline.Cursor.Action == CursorView.CursorAction.Free
            && main is { ItemCount: 0, Rect: not null }
            && off is { ItemCount: 0 }
            && serverMain is { ItemCount: 0 }
            && serverOff is { ItemCount: 0 };
        context.Check(prepared, "prepared empty active weapon set",
            $"inventory={baseline.Inventory.IsOpen} cursor={baseline.Cursor.Action} " +
            $"mainUi={Describe(main)} offUi={Describe(off)} mainServer={Describe(serverMain)} offServer={Describe(serverOff)}");
        if (!prepared || main?.Rect is not { } mainRect)
            return LiveTestCaseResult.Blocked("active main/off hand or cursor did not match the prepared state", "PreparedStateMismatch");

        var candidates = baseline.Inventory.Items.Where(item => item.StackSize == 1
                && item.Rect is { Width: > 0, Height: > 0 }
                && item.Path.StartsWith("Metadata/Items/Weapons/", StringComparison.Ordinal))
            .OrderByDescending(item => ItemSocketsReader.TryRead(baseline.Reader, item.ItemEntity, out var sockets)
                ? sockets.SocketedGems.Count : -1)
            .ThenBy(item => item.Rect!.Value.Y).ThenBy(item => item.Rect!.Value.X).ToArray();
        context.Check(candidates.Length > 0, "inventory weapon candidate", $"count={candidates.Length}");
        if (candidates.Length == 0 || candidates[0].Rect is not { } sourceRect)
            return LiveTestCaseResult.Blocked("no inventory weapon is available", "NoWeaponCandidate");

        var target = candidates[0];
        var targetIdentity = ItemIdentityFingerprint.Capture(baseline.Reader, target);
        var identityMultiplicity = baseline.Inventory.Items.Count(item =>
            ItemIdentityFingerprint.Capture(baseline.Reader, item).Canonical == targetIdentity.Canonical);
        var inventoryBefore = PositionalFingerprint(baseline.Inventory);
        var inventoryWhileMoved = PositionalFingerprint(baseline.Inventory.Items
            .Where(item => item.ElementAddress != target.ElementAddress));
        context.Observe("target item fingerprint",
            $"base='{targetIdentity.BaseName}' multiplicity={identityMultiplicity} canonical=[{targetIdentity.Canonical}] " +
            $"socketCoverage={(targetIdentity.SocketDetailsValidated ? "validated" : "pending")}");
        context.Check(!string.IsNullOrWhiteSpace(targetIdentity.BaseName), "target base name", targetIdentity.BaseName);
        if (string.IsNullOrWhiteSpace(targetIdentity.BaseName))
            return LiveTestCaseResult.Blocked("weapon base identity is unreadable", "CandidateUnreadable");

        var sourceTooltip = await HoverAndReadTooltipAsync(context, target, "source weapon", cancellationToken);
        context.Check(sourceTooltip.Length > 0 && sourceTooltip.Contains(targetIdentity.BaseName, StringComparison.OrdinalIgnoreCase),
            "source tooltip identity", sourceTooltip);
        if (sourceTooltip.Length == 0)
            return LiveTestCaseResult.Fail("source weapon tooltip could not be fingerprinted", "TooltipMismatch");

        var sourcePoint = ToScreen(baseline.Window, sourceRect);
        var mainPoint = ToScreen(baseline.Window, mainRect);
        var pickup = await context.VerifiedClickAsync(sourcePoint.X, sourcePoint.Y, ClickIntent.InteractUi,
            $"pick up '{targetIdentity.BaseName}' from exact inventory cell",
            () =>
            {
                var current = context.Snapshot();
                return current.Cursor.Action == CursorView.CursorAction.HoldItem
                    && PositionalFingerprint(current.Inventory) == inventoryWhileMoved
                    && EquipmentIsEmpty(current, MainHandUiIndex, MainHandServerType, MainHandServerSlot);
            }, 3_000, cancellationToken);
        if (pickup != ActionOutcome.Confirmed)
            return await FailWithRecoveryAsync(context, sourcePoint, mainPoint, inventoryBefore,
                "weapon pickup transition was not proven", "PickupMismatch", cancellationToken);
        if (!await context.WaitForInputIdleAsync("after inventory weapon pickup", 1_500, cancellationToken))
            return await FailWithRecoveryAsync(context, sourcePoint, mainPoint, inventoryBefore,
                "input did not settle after weapon pickup", "InputSettleFailed", cancellationToken);

        var place = await context.VerifiedClickAsync(mainPoint.X, mainPoint.Y, ClickIntent.InteractUi,
            $"place held '{targetIdentity.BaseName}' into active main hand",
            () =>
            {
                var current = context.Snapshot();
                var currentLayout = EquipmentInventoriesView.From(current);
                var currentMain = Ui(currentLayout, MainHandUiIndex);
                var currentServer = Server(currentLayout, MainHandServerType, MainHandServerSlot);
                return current.Cursor.Action == CursorView.CursorAction.Free
                    && PositionalFingerprint(current.Inventory) == inventoryWhileMoved
                    && currentMain is { ItemCount: 1 } && currentMain.Value.Items.Count == 1
                    && currentServer is { ItemCount: 1 };
            }, 3_000, cancellationToken);
        if (place != ActionOutcome.Confirmed)
            return await FailWithRecoveryAsync(context, sourcePoint, mainPoint, inventoryBefore,
                "main-hand placement transition was not proven", "EquipMismatch", cancellationToken);
        if (!await context.WaitForInputIdleAsync("after main-hand placement", 1_500, cancellationToken))
            return await FailWithRecoveryAsync(context, sourcePoint, mainPoint, inventoryBefore,
                "input did not settle after main-hand placement", "InputSettleFailed", cancellationToken);

        var equippedSnapshot = context.Snapshot();
        var equippedLayout = EquipmentInventoriesView.From(equippedSnapshot);
        var equippedMain = Ui(equippedLayout, MainHandUiIndex);
        if (equippedMain is not { Items.Count: 1 })
            return await FailWithRecoveryAsync(context, sourcePoint, mainPoint, inventoryBefore,
                "equipped main hand had no readable item", "EquipIdentityUnreadable", cancellationToken);
        var equippedItem = equippedMain.Value.Items[0];
        var equippedIdentity = ItemIdentityFingerprint.Capture(equippedSnapshot.Reader, equippedItem);
        context.Check(equippedIdentity.Canonical == targetIdentity.Canonical,
            "location-independent item fingerprint preserved",
            $"before=[{targetIdentity.Canonical}] equipped=[{equippedIdentity.Canonical}]");
        context.Check(equippedItem.ItemEntity != target.ItemEntity,
            "entity rematerialization observed",
            $"inventoryEntity=0x{(long)target.ItemEntity:X} equippedEntity=0x{(long)equippedItem.ItemEntity:X}");

        var equippedTooltip = await HoverAndReadTooltipAsync(context, equippedItem, "equipped weapon", cancellationToken);
        context.Check(equippedTooltip == sourceTooltip, "exact tooltip fingerprint preserved",
            $"source=[{sourceTooltip}] equipped=[{equippedTooltip}]");
        if (equippedIdentity.Canonical != targetIdentity.Canonical || equippedTooltip != sourceTooltip)
            return await FailWithRecoveryAsync(context, sourcePoint, mainPoint, inventoryBefore,
                "equipped item identity did not match the inventory source", "EquipIdentityMismatch", cancellationToken);

        var remove = await context.VerifiedClickAsync(mainPoint.X, mainPoint.Y, ClickIntent.InteractUi,
            $"pick up equipped '{targetIdentity.BaseName}' from active main hand",
            () =>
            {
                var current = context.Snapshot();
                return current.Cursor.Action == CursorView.CursorAction.HoldItem
                    && PositionalFingerprint(current.Inventory) == inventoryWhileMoved
                    && EquipmentIsEmpty(current, MainHandUiIndex, MainHandServerType, MainHandServerSlot);
            }, 3_000, cancellationToken);
        if (remove != ActionOutcome.Confirmed)
            return await FailWithRecoveryAsync(context, sourcePoint, mainPoint, inventoryBefore,
                "equipped weapon pickup was not proven", "UnequipMismatch", cancellationToken);
        if (!await context.WaitForInputIdleAsync("after equipped weapon pickup", 1_500, cancellationToken))
            return await FailWithRecoveryAsync(context, sourcePoint, mainPoint, inventoryBefore,
                "input did not settle after equipped weapon pickup", "InputSettleFailed", cancellationToken);

        var restore = await context.VerifiedClickAsync(sourcePoint.X, sourcePoint.Y, ClickIntent.InteractUi,
            $"restore '{targetIdentity.BaseName}' to its exact original inventory cell",
            () =>
            {
                var current = context.Snapshot();
                return current.Cursor.Action == CursorView.CursorAction.Free
                    && PositionalFingerprint(current.Inventory) == inventoryBefore
                    && EquipmentIsEmpty(current, MainHandUiIndex, MainHandServerType, MainHandServerSlot);
            }, 3_000, cancellationToken);
        if (restore != ActionOutcome.Confirmed)
            return await FailWithRecoveryAsync(context, sourcePoint, mainPoint, inventoryBefore,
                "original inventory cell was not restored", "RestoreFailed", cancellationToken);
        if (!await context.WaitForInputIdleAsync("after exact-cell restoration", 1_500, cancellationToken))
            return LiveTestCaseResult.Fail("input did not settle after exact-cell restoration", "InputSettleFailed");

        var final = context.Snapshot();
        var finalIdentities = IdentityMultiset(final);
        var initialIdentities = IdentityMultiset(baseline);
        context.Check(final.Cursor.Action == CursorView.CursorAction.Free, "cursor restored", $"action={final.Cursor.Action}");
        context.Check(PositionalFingerprint(final.Inventory) == inventoryBefore,
            "exact inventory geometry restored", PositionalFingerprint(final.Inventory));
        context.Check(finalIdentities == initialIdentities, "item identity multiset restored",
            $"before=[{initialIdentities}] after=[{finalIdentities}]");
        context.Check(EquipmentIsEmpty(final, MainHandUiIndex, MainHandServerType, MainHandServerSlot),
            "active main hand restored empty", "UI and server counts are zero");
        return LiveTestCaseResult.Pass(
            $"equipped and removed '{targetIdentity.BaseName}' using normal clicks; UI/server state, canonical identity, exact tooltip, cursor, and original inventory geometry were proven and restored (identity multiplicity {identityMultiplicity})",
            "CompletedAndRestored");
    }

    private static async Task<string> HoverAndReadTooltipAsync(
        LiveTestContext context, InventoryView.Item item, string label, CancellationToken cancellationToken)
    {
        if (item.Rect is not { } rect) return string.Empty;
        var point = ToScreen(context.Snapshot().Window, rect);
        await context.HoverAsync(point.X, point.Y, 150, cancellationToken);
        var reached = await context.WaitUntilAsync(label + " hover",
            () => ResolvesHoverTo(context.Snapshot(), item.ElementAddress), 1_500, cancellationToken, 20);
        var snapshot = context.Snapshot();
        snapshot.Reader.TryReadStruct<nint>(item.ElementAddress + KnownOffsets.Element.RenderedTooltip, out var tooltip);
        var fingerprint = ReadTooltip(snapshot.Reader, tooltip);
        context.Check(reached, label + " exact UIHover", $"element=0x{(long)item.ElementAddress:X}");
        context.Check(tooltip != 0 && fingerprint.Length > 0, label + " rendered tooltip",
            $"root=0x{(long)tooltip:X} text=[{fingerprint}]");
        return reached ? fingerprint : string.Empty;
    }

    private static string ReadTooltip(MemoryReader reader, nint root)
    {
        if (root == 0) return string.Empty;
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
                var text = NativeString.Read(reader, address + KnownOffsets.Element.TextNoTags);
                if (string.IsNullOrWhiteSpace(text))
                    text = NativeString.Read(reader, address + KnownOffsets.Element.Text);
                foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (!string.IsNullOrWhiteSpace(line)) lines.Add(line);
            }
            if (depth >= 14) continue;
            foreach (var child in element.Children) queue.Enqueue((child, depth + 1));
        }
        return string.Join(" || ", lines);
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

    private static async Task<LiveTestCaseResult> FailWithRecoveryAsync(
        LiveTestContext context, (int X, int Y) source, (int X, int Y) main,
        string inventoryBefore, string failure, string classification, CancellationToken cancellationToken)
    {
        var state = context.Snapshot();
        if (state.Cursor.Action == CursorView.CursorAction.Free
            && !EquipmentIsEmpty(state, MainHandUiIndex, MainHandServerType, MainHandServerSlot))
        {
            await context.VerifiedClickAsync(main.X, main.Y, ClickIntent.InteractUi, "recovery pick up main-hand item",
                () => context.Snapshot().Cursor.Action == CursorView.CursorAction.HoldItem,
                3_000, cancellationToken);
            await context.WaitForInputIdleAsync("after recovery main-hand pickup", 1_500, cancellationToken);
        }
        if (context.Snapshot().Cursor.Action == CursorView.CursorAction.HoldItem)
        {
            await context.VerifiedClickAsync(source.X, source.Y, ClickIntent.InteractUi, "recovery restore original inventory cell",
                () =>
                {
                    var current = context.Snapshot();
                    return current.Cursor.Action == CursorView.CursorAction.Free
                        && PositionalFingerprint(current.Inventory) == inventoryBefore
                        && EquipmentIsEmpty(current, MainHandUiIndex, MainHandServerType, MainHandServerSlot);
                }, 3_000, cancellationToken);
            await context.WaitForInputIdleAsync("after recovery inventory placement", 1_500, cancellationToken);
        }
        var final = context.Snapshot();
        var restored = final.Cursor.Action == CursorView.CursorAction.Free
            && PositionalFingerprint(final.Inventory) == inventoryBefore
            && EquipmentIsEmpty(final, MainHandUiIndex, MainHandServerType, MainHandServerSlot);
        context.Check(restored, "failure-path equipment restoration", restored ? "baseline restored" : "baseline mismatch");
        return LiveTestCaseResult.Fail(restored ? failure + "; baseline restored" : failure + "; baseline not restored",
            restored ? classification : "RestoreFailed");
    }

    private static bool EquipmentIsEmpty(GameSnapshot snapshot, int uiIndex, int serverType, int serverSlot)
    {
        var layout = EquipmentInventoriesView.From(snapshot);
        return Ui(layout, uiIndex) is { ItemCount: 0 }
            && Server(layout, serverType, serverSlot) is { ItemCount: 0 };
    }

    private static EquipmentInventoriesView.UiInventory? Ui(EquipmentInventoriesView view, int index)
        => view.UiInventories.FirstOrDefault(x => x.Index == index) is { Address: not 0 } value ? value : null;

    private static EquipmentInventoriesView.ServerInventory? Server(EquipmentInventoriesView view, int type, int slot)
        => view.ServerInventories.FirstOrDefault(x => x.InventoryType == type && x.InventorySlot == slot) is { Address: not 0 } value ? value : null;

    private static string PositionalFingerprint(InventoryView inventory)
        => PositionalFingerprint(inventory.Items);

    private static string PositionalFingerprint(IEnumerable<InventoryView.Item> items)
        => string.Join('|', items.OrderBy(x => x.Rect?.Y ?? float.MaxValue).ThenBy(x => x.Rect?.X ?? float.MaxValue)
            .Select(x => $"{x.Path}:{x.StackSize}:{x.Width}x{x.Height}:{Format(x.Rect)}"));

    private static string IdentityMultiset(GameSnapshot snapshot)
        => string.Join("||", snapshot.Inventory.Items
            .Select(item => ItemIdentityFingerprint.Capture(snapshot.Reader, item).Canonical)
            .OrderBy(x => x, StringComparer.Ordinal));

    private static string Format(ElementGeometry.Rect? rect)
        => rect is { } value ? $"{value.X:F2},{value.Y:F2},{value.Width:F2},{value.Height:F2}" : "none";

    private static (int X, int Y) ToScreen(WindowInfo window, ElementGeometry.Rect rect)
        => (window.OriginX + (int)MathF.Round(rect.CenterX), window.OriginY + (int)MathF.Round(rect.CenterY));

    private static string Describe(EquipmentInventoriesView.UiInventory? value)
        => value is { } inventory ? $"index={inventory.Index} count={inventory.ItemCount} rect={inventory.Rect}" : "missing";

    private static string Describe(EquipmentInventoriesView.ServerInventory? value)
        => value is { } inventory ? $"type={inventory.InventoryType} slot={inventory.InventorySlot} count={inventory.ItemCount}" : "missing";
}
