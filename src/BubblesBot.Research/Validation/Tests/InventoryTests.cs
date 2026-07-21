using BubblesBot.Core.Game;
using BubblesBot.Core;

namespace BubblesBot.Research.Validation.Tests;

public sealed class PlayerInventoryOracleTest : ValidationTest
{
    public override string Name => "InventoryPanel player inventory items";
    public override string? Group => "Inventory/stash";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(StateKeys.IngameUi, out var uiObj) || uiObj is not nint ingameUi)
            return new TestOutcome.Skip(Name, "IngameUi not resolved");
        if (!ctx.Reader.TryReadStruct<nint>(ingameUi + KnownOffsets.IngameUiElements.InventoryPanel, out var inventoryPanel))
            return new TestOutcome.Fail(Name, "could not read InventoryPanel pointer");
        if (!InventoryReader.TryGetPlayerInventory(ctx.Reader, inventoryPanel, out var playerInventory))
        {
            var expected = await ctx.Poemcp.EvalAsync(
                "IngameState.IngameUi.InventoryPanel[ExileCore.Shared.Enums.InventoryIndex.PlayerInventory].Address.ToString(\"X\")", ct);
            if (expected.Success)
            {
                var expectedInventoryAddress = (nint)long.Parse(expected.AsString(), System.Globalization.NumberStyles.HexNumber);
                return new TestOutcome.Fail(Name, $"could not resolve player inventory; candidates=[{string.Join(", ", FindPointerCandidates(ctx.Reader, inventoryPanel, expectedInventoryAddress))}]");
            }
            return new TestOutcome.Fail(Name, "could not resolve player inventory");
        }

        var snapshot = InventoryReader.TryReadInventory(ctx.Reader, playerInventory);
        if (snapshot is null)
            return new TestOutcome.Fail(Name, $"could not read inventory at 0x{playerInventory:X}");

        var truth = await ctx.Poemcp.EvalAsync(
            """
            var inv = IngameState.IngameUi.InventoryPanel[ExileCore.Shared.Enums.InventoryIndex.PlayerInventory];
            var items = inv.VisibleInventoryItems;
            inv.Address.ToString("X") + "|" +
            inv.ItemCount + "|" +
            (items == null ? "null" : string.Join(";", items.Select(i =>
                i.Address.ToString("X") + "," +
                i.Item.Address.ToString("X") + "," +
                i.ItemWidth + "," +
                i.ItemHeight)))
            """, ct);
        if (!truth.Success)
            return new TestOutcome.Skip(Name, $"POEMCP unavailable: {truth.Error}");

        var parts = truth.AsString().Split('|');
        if (parts.Length != 3)
            return new TestOutcome.Fail(Name, $"unexpected POEMCP result: {truth.AsString()}");

        var expectedAddress = (nint)long.Parse(parts[0], System.Globalization.NumberStyles.HexNumber);
        var expectedItemCount = long.Parse(parts[1]);
        if (snapshot.Address != expectedAddress)
            return new TestOutcome.Fail(Name, $"address mismatch ours=0x{snapshot.Address:X}, POEMCP=0x{expectedAddress:X}");
        if (snapshot.ItemCount != expectedItemCount)
            return new TestOutcome.Fail(Name, $"ItemCount mismatch ours={snapshot.ItemCount}, POEMCP={expectedItemCount}");

        var expectedItems = parts[2] == "null" || parts[2].Length == 0
            ? Array.Empty<(nint Address, nint Item, int Width, int Height)>()
            : parts[2].Split(';').Select(ParseItem).ToArray();
        if (snapshot.VisibleItems.Count != expectedItems.Length)
            return new TestOutcome.Fail(Name, $"VisibleInventoryItems count mismatch ours={snapshot.VisibleItems.Count}, POEMCP={expectedItems.Length}");

        for (var i = 0; i < expectedItems.Length; i++)
        {
            var ours = snapshot.VisibleItems[i];
            var expected = expectedItems[i];
            if (ours.Address != expected.Address || ours.ItemEntity != expected.Item || ours.Width != expected.Width || ours.Height != expected.Height)
                return new TestOutcome.Fail(Name, $"item #{i} mismatch ours=0x{ours.Address:X}/0x{ours.ItemEntity:X}/{ours.Width}x{ours.Height}, POEMCP=0x{expected.Address:X}/0x{expected.Item:X}/{expected.Width}x{expected.Height}");
        }

        return new TestOutcome.Pass(Name, $"items={snapshot.VisibleItems.Count}, size={snapshot.Size.X}x{snapshot.Size.Y}");
    }

    private static (nint Address, nint Item, int Width, int Height) ParseItem(string value)
    {
        var parts = value.Split(',');
        return ((nint)long.Parse(parts[0], System.Globalization.NumberStyles.HexNumber),
            (nint)long.Parse(parts[1], System.Globalization.NumberStyles.HexNumber),
            int.Parse(parts[2]),
            int.Parse(parts[3]));
    }

    private static IReadOnlyList<string> FindPointerCandidates(MemoryReader reader, nint baseAddress, nint expected)
    {
        var result = new List<string>();
        for (var off = 0; off < 0x1000; off += 8)
        {
            if (reader.TryReadStruct<nint>(baseAddress + off, out var candidate) && candidate == expected)
                result.Add($"+0x{off:X}");
        }
        return result;
    }
}

// NOTE: A "visible stash items" oracle is intentionally NOT included yet. The stash UI element
// reuses the same Inventory layout as player inventory at the surface level, but ItemCount and
// VisibleInventoryItems are sourced from the linked ServerInventory object, not from the
// +0x480 field that works for player inventory. Wiring this needs a ServerInventory pointer
// chain we haven't mapped yet. Tracked in PLANNING.md "next steps".

public sealed class VisibleStashRootOracleTest : ValidationTest
{
    public override string Name => "StashElement visible stash root";
    public override string? Group => "Inventory/stash";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(StateKeys.IngameUi, out var uiObj) || uiObj is not nint ingameUi)
            return new TestOutcome.Skip(Name, "IngameUi not resolved");
        if (!ctx.Reader.TryReadStruct<nint>(ingameUi + KnownOffsets.IngameUiElements.StashElement, out var stashElement))
            return new TestOutcome.Fail(Name, "could not read StashElement pointer");

        if (!StashReader.TryGetVisibleStash(ctx.Reader, stashElement, out var visibleStash, out var visibleIndex, out var totalStashes))
            return new TestOutcome.Skip(Name, "stash inventory panel not visible/resolved");

        var truth = await ctx.Poemcp.EvalAsync(
            """
            var s = IngameState.IngameUi.StashElement;
            var inv = s.VisibleStash;
            s.TotalStashes + "|" +
            s.IndexVisibleStash + "|" +
            inv.Address.ToString("X")
            """, ct);
        if (!truth.Success)
            return new TestOutcome.Skip(Name, $"POEMCP unavailable: {truth.Error}");

        var parts = truth.AsString().Split('|');
        if (parts.Length != 3)
            return new TestOutcome.Fail(Name, $"unexpected POEMCP result: {truth.AsString()}");

        var expectedTotal = int.Parse(parts[0]);
        var expectedIndex = int.Parse(parts[1]);
        var expectedVisible = (nint)long.Parse(parts[2], System.Globalization.NumberStyles.HexNumber);

        if (totalStashes != expectedTotal || visibleIndex != expectedIndex || visibleStash != expectedVisible)
            return new TestOutcome.Fail(Name, $"ours total/index/address={totalStashes}/{visibleIndex}/0x{visibleStash:X}, POEMCP={expectedTotal}/{expectedIndex}/0x{expectedVisible:X}");

        return new TestOutcome.Pass(Name, $"tabs={totalStashes}, index={visibleIndex}, visibleStash=0x{visibleStash:X}");
    }
}
