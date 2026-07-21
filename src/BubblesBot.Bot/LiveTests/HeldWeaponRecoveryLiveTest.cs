using BubblesBot.Bot.Input;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Guarded recovery for a 1x3 weapon left on the cursor by equipment research.</summary>
public sealed class HeldWeaponRecoveryLiveTest : ILiveTestCase
{
    public string Id => "A-06-held-weapon-recover";
    public string Name => "Held 1x3 weapon recovery";
    public string Description => "Finds the first empty 1x3 inventory region and places the currently held research weapon there.";
    public string ManualSetup => "Inventory open, cursor holding the 1x3 weapon from A-06 research, active main hand empty, PoE focused.";
    public LiveTestMutation Mutation => LiveTestMutation.Reversible;
    public bool DrivesInput => true;

    public async Task<LiveTestCaseResult> RunAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        var before = context.Snapshot();
        if (!before.Inventory.IsOpen || before.Cursor.Action != CursorView.CursorAction.HoldItem)
            return LiveTestCaseResult.Blocked("inventory must be open with the weapon held", "PreparedStateMismatch");
        var grid = EquipmentInventoriesView.From(before).UiInventories.FirstOrDefault(x => x.Index == 19);
        if (grid.Address == 0 || grid.Rect is not { } gridRect || grid.Size.X <= 0 || grid.Size.Y <= 0)
            return LiveTestCaseResult.Blocked("player inventory grid is unreadable", "InventoryUnreadable");

        var occupied = new bool[grid.Size.X, grid.Size.Y];
        foreach (var item in before.Inventory.Items)
        {
            if (item.Rect is not { } rect) continue;
            var column = (int)MathF.Round((rect.X - gridRect.X) / (gridRect.Width / grid.Size.X));
            var row = (int)MathF.Round((rect.Y - gridRect.Y) / (gridRect.Height / grid.Size.Y));
            for (var x = column; x < column + item.Width && x < grid.Size.X; x++)
                for (var y = row; y < row + item.Height && y < grid.Size.Y; y++)
                    if (x >= 0 && y >= 0) occupied[x, y] = true;
        }
        (int Column, int Row)? target = null;
        for (var row = 0; row <= grid.Size.Y - 3 && target is null; row++)
            for (var column = 0; column < grid.Size.X; column++)
                if (!occupied[column, row] && !occupied[column, row + 1] && !occupied[column, row + 2])
                { target = (column, row); break; }
        if (target is null)
            return LiveTestCaseResult.Blocked("no empty 1x3 inventory region exists", "NoRecoverySpace");

        var cellW = gridRect.Width / grid.Size.X;
        var cellH = gridRect.Height / grid.Size.Y;
        var expectedRect = new ElementGeometry.Rect(
            gridRect.X + target.Value.Column * cellW,
            gridRect.Y + target.Value.Row * cellH,
            cellW, cellH * 3);
        var point = before.Window.ToScreen(expectedRect.CenterX, expectedRect.CenterY);
        var countBefore = before.Inventory.Items.Count;
        var outcome = await context.VerifiedClickAsync(point.X, point.Y, ClickIntent.InteractUi,
            "place held 1x3 research weapon into verified empty inventory region",
            () =>
            {
                var current = context.Snapshot();
                return current.Cursor.Action == CursorView.CursorAction.Free
                    && current.Inventory.Items.Count == countBefore + 1
                    && current.Inventory.Items.Any(item => item.Width == 1 && item.Height == 3
                        && item.Path.StartsWith("Metadata/Items/Weapons/", StringComparison.Ordinal)
                        && SameRect(item.Rect, expectedRect));
            }, 3_000, cancellationToken);
        await context.WaitForInputIdleAsync("after held-weapon recovery", 1_500, cancellationToken);
        return outcome == ActionOutcome.Confirmed
            ? LiveTestCaseResult.Pass($"held weapon placed into empty region column={target.Value.Column} row={target.Value.Row}", "Recovered")
            : LiveTestCaseResult.Fail("held weapon could not be placed into the verified empty region", "RestoreFailed");
    }

    private static bool SameRect(ElementGeometry.Rect? actual, ElementGeometry.Rect expected)
        => actual is { } rect && MathF.Abs(rect.X - expected.X) < 1f && MathF.Abs(rect.Y - expected.Y) < 1f
            && MathF.Abs(rect.Width - expected.Width) < 1f && MathF.Abs(rect.Height - expected.Height) < 1f;
}
