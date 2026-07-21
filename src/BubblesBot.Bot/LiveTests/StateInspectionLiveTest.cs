using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>
/// Read-only setup fingerprint. This is the first command used after the operator prepares a
/// situation: it prints the exact character and area hash needed by a later input-capable run.
/// </summary>
public sealed class StateInspectionLiveTest : ILiveTestCase
{
    public string Id => "setup-inspect";
    public string Name => "Prepared-state inspection";
    public string Description => "Read-only character/area/HP/panel fingerprint; sends no input.";
    public string ManualSetup => "Any loaded in-game area. No setup confirmation or --arm is needed.";
    public LiveTestMutation Mutation => LiveTestMutation.ReadOnly;
    public bool DrivesInput => false;
    public IReadOnlySet<string> AllowedBlockingPanels => OpenPanelsView.BlockingPanels;

    public Task<LiveTestCaseResult> RunAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = context.Snapshot();
        var player = snapshot.Player;
        var openPanels = snapshot.OpenPanels.Open;
        var liveSkills = snapshot.LiveSkills.Entries;
        var npcLabels = snapshot.GroundLabels
            .Where(x => x.Path.Contains("/NPC/", StringComparison.OrdinalIgnoreCase))
            .Select(x => $"{x.RenderName}[{x.Path}] visible={x.IsLabelVisible} onScreen={x.IsRectOnScreen} distance={x.DistanceToPlayer:F0} rect={x.LabelRect}")
            .ToArray();
        var inventory = snapshot.Inventory;
        var inventoryItems = inventory.Items.Select(x =>
            $"{x.Path} stack={x.StackSize} size={x.Width}x{x.Height} rect={x.Rect}").ToArray();
        var stash = snapshot.StashInventory;
        var stashItems = stash.Items.Select(x =>
            $"{x.Path} stack={x.StackSize} size={x.Width}x{x.Height} rect={x.Rect}").ToArray();

        context.Observe("prepared setup", $"characterPresent={!string.IsNullOrWhiteSpace(player?.CharacterName)} areaHash=0x{snapshot.AreaHash:X8}",
            new Dictionary<string, object?>
            {
                ["characterPresent"] = !string.IsNullOrWhiteSpace(player?.CharacterName),
                ["areaHash"] = $"0x{snapshot.AreaHash:X8}",
                ["league"] = snapshot.League,
                ["gridX"] = player?.GridPosition.X,
                ["gridY"] = player?.GridPosition.Y,
                ["hp"] = player?.Life.Current,
                ["hpMax"] = player?.Life.Max,
                ["openPanels"] = openPanels.ToArray(),
                ["skillSlots"] = liveSkills.Count,
            });
        context.Observe("NPC labels", npcLabels.Length == 0 ? "none" : string.Join(" | ", npcLabels));
        context.Observe("inventory", inventory.IsOpen
            ? $"open occupied={inventory.OccupiedCells}/60 items=[{string.Join(" | ", inventoryItems)}]"
            : "closed");
        context.Observe("stash", stash.IsOpen
            ? $"open visibleTabIndex={stash.VisibleTabIndex} totalTabs={stash.TotalTabs} items=[{string.Join(" | ", stashItems)}]"
            : "closed");
        context.Observe("cursor",
            $"readable={snapshot.Cursor.IsReadable} address=0x{(long)snapshot.Cursor.Address:X} action={snapshot.Cursor.Action}");

        var ok = true;
        ok &= context.Check(player is not null, "player", player is null ? "missing" : "present");
        ok &= context.Check(!string.IsNullOrWhiteSpace(player?.CharacterName), "character name",
            string.IsNullOrWhiteSpace(player?.CharacterName) ? "missing" : "present");
        ok &= context.Check(snapshot.AreaHash != 0, "area hash", $"0x{snapshot.AreaHash:X8}");
        ok &= context.Check(player is not null && player.Life.Max > 0 && player.Life.Current > 0,
            "life", player is null ? "missing" : $"{player.Life.Current}/{player.Life.Max}");
        ok &= context.Check(snapshot.Window.IsValid, "window", snapshot.Window.IsValid
            ? $"{snapshot.Window.Width}x{snapshot.Window.Height} at ({snapshot.Window.OriginX},{snapshot.Window.OriginY})"
            : "invalid");

        return Task.FromResult(ok
            ? LiveTestCaseResult.Pass("prepared state fingerprint recorded", "ReadOnlyFingerprint")
            : LiveTestCaseResult.Fail("prepared state could not be fingerprinted", "ReadContractFailed"));
    }
}
