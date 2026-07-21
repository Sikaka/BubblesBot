using BubblesBot.Core;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Read-only correlation of visible equipment widgets and server inventory holders.</summary>
public sealed class EquipmentSlotsInspectLiveTest : ILiveTestCase
{
    public string Id => "A-06-equipment-slots-inspect";
    public string Name => "Equipment slot layout inspection";
    public string Description => "Enumerates plausible InventoryPanel inventories and server PlayerInventories holders without sending input.";
    public string ManualSetup => "Open inventory on the intended weapon set; leave the target equipment slot empty and the cursor free.";
    public LiveTestMutation Mutation => LiveTestMutation.ReadOnly;
    public bool DrivesInput => false;
    public IReadOnlySet<string> AllowedBlockingPanels => OpenPanelsView.BlockingPanels;

    public Task<LiveTestCaseResult> RunAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = context.Snapshot();
        var view = EquipmentInventoriesView.From(snapshot);
        context.Check(view.InventoryPanelOpen, "inventory panel open", $"open={view.InventoryPanelOpen}");
        context.Check(snapshot.Cursor.Action == CursorView.CursorAction.Free,
            "cursor free", $"action={snapshot.Cursor.Action}");

        foreach (var inventory in view.UiInventories.OrderBy(x => x.Index))
        {
            var items = inventory.Items.Select(item =>
                $"{ReadBaseName(snapshot, item)}<{item.Path}> {item.Width}x{item.Height} rect={item.Rect}");
            context.Observe("UI inventory",
                $"index={inventory.Index} address=0x{(long)inventory.Address:X} visible={inventory.IsVisible} " +
                $"size={inventory.Size.X}x{inventory.Size.Y} count={inventory.ItemCount} rect={inventory.Rect} " +
                $"items=[{string.Join(" | ", items)}]");
        }
        foreach (var inventory in view.ServerInventories.OrderBy(x => x.HolderIndex))
            context.Observe("server inventory",
                $"holderIndex={inventory.HolderIndex} holderId={inventory.HolderId} address=0x{(long)inventory.Address:X} " +
                $"type={inventory.InventoryType} slot={inventory.InventorySlot} size={inventory.Columns}x{inventory.Rows} count={inventory.ItemCount}");

        var plausibleEquipment = view.UiInventories.Where(x => x.Index != 19
            && x.IsVisible && x.Rect is { Width: > 20, Height: > 20 }
            && x.Size.X is >= 1 and <= 2 && x.Size.Y is >= 1 and <= 4).ToArray();
        var ok = view.InventoryPanelOpen && snapshot.Cursor.Action == CursorView.CursorAction.Free
            && view.UiInventories.Any(x => x.Index == 19)
            && plausibleEquipment.Length > 0 && view.ServerInventories.Count > 0;
        context.Check(view.UiInventories.Any(x => x.Index == 19), "player inventory index", "expected index=19");
        context.Check(plausibleEquipment.Length > 0, "visible equipment candidates",
            string.Join(", ", plausibleEquipment.Select(x => $"{x.Index}:{x.Size.X}x{x.Size.Y}@{x.Rect}")));
        context.Check(view.ServerInventories.Count > 0, "server inventory holders",
            $"count={view.ServerInventories.Count}");
        return Task.FromResult(ok
            ? LiveTestCaseResult.Pass("equipment UI and server-holder layouts recorded", "ReadOnlyFingerprint")
            : LiveTestCaseResult.Fail("equipment slot layout could not be correlated", "ReadContractFailed"));
    }

    private static string ReadBaseName(GameSnapshot snapshot, InventoryView.Item item)
    {
        var components = BubblesBot.Core.Game.EntityComponents.ReadComponentMap(snapshot.Reader, item.ItemEntity);
        if (!components.TryGetValue("Base", out var component)
            || !snapshot.Reader.TryReadStruct<nint>(component + BubblesBot.Core.Game.KnownOffsets.BaseComponent.ItemInfo, out var info)
            || info == 0)
            return string.Empty;
        return NativeString.Read(snapshot.Reader,
            info + BubblesBot.Core.Game.KnownOffsets.ItemInfo.BaseName);
    }
}
