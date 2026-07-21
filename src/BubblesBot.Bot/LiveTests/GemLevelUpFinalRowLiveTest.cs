using BubblesBot.Bot.Input;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Consumes the sole remaining Chance to Bleed level-up row and proves panel closure.</summary>
public sealed class GemLevelUpFinalRowLiveTest : ILiveTestCase
{
    private const string TargetPath = "Metadata/Items/Gems/SupportGemChanceToBleed";

    public string Id => "U-07-gem-level-up-final-row";
    public string Name => "Final gem-row level-up";
    public string Description => "Levels the exact sole Chance to Bleed row and proves the gem remains in its socket while the level-up panel disappears.";
    public string ManualSetup => "Have level-1 Chance to Bleed as the sole ready-to-level row, with no All control. This consumes its pending level-up.";
    public LiveTestMutation Mutation => LiveTestMutation.Irreversible;
    public bool DrivesInput => true;

    public async Task<LiveTestCaseResult> RunAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        var baseline = context.Snapshot();
        var panel = GemLevelUpView.Read(baseline.Reader, baseline.IngameStateAddress);
        var targets = panel.Rows.Where(x => x.Gem.MetadataPath == TargetPath).ToArray();
        var prepared = panel.IsVisible && panel.Rows.Count == 1 && panel.AllControl is null
            && targets.Length == 1 && targets[0].Gem.Level == 1;
        if (!prepared)
            return Reconcile(context, baseline, panel);
        context.Check(true, "prepared sole-row fixture",
            $"rows={panel.Rows.Count} all={panel.AllControl is not null} gem='{targets[0].Gem.BaseName}' level={targets[0].Gem.Level}");

        var target = targets[0];
        var locations = FindSocketedGems(baseline).Where(x => x.Gem.EntityAddress == target.GemEntity).ToArray();
        context.Check(locations.Length == 1, "row entity resolves to one equipped socket",
            $"entity=0x{(long)target.GemEntity:X} matches={locations.Length}");
        if (locations.Length != 1)
            return LiveTestCaseResult.Blocked("sole row did not bind to one equipped socket", "SocketIdentityMismatch");
        var location = locations[0];

        var point = baseline.Window.ToScreen(target.LevelControl.Rect.CenterX, target.LevelControl.Rect.CenterY);
        await context.HoverAsync(point.X, point.Y, 180, cancellationToken);
        var exactHover = await context.WaitUntilAsync("exact Chance to Bleed level control hover",
            () => ResolvesHoverTo(context.Snapshot(), target.LevelControl.Element),
            1_500, cancellationToken, 20);
        if (!exactHover)
            return LiveTestCaseResult.Fail("cursor did not resolve to Chance to Bleed's exact level control", "TargetHoverMismatch");

        var final = GemLevelUpView.Read(context.Snapshot().Reader, context.Snapshot().IngameStateAddress)
            .Rows.SingleOrDefault(x => x.GemEntity == target.GemEntity);
        context.Check(final?.Gem.MetadataPath == TargetPath && final.Gem.Level == 1,
            "final pre-click row identity",
            final is null ? "missing" : $"entity=0x{(long)final.GemEntity:X} gem='{final.Gem.BaseName}' level={final.Gem.Level}");
        if (final is null || final.Gem.MetadataPath != TargetPath || final.Gem.Level != 1)
            return LiveTestCaseResult.Fail("target identity changed before dispatch", "PreClickIdentityMismatch");

        var click = await context.VerifiedClickAsync(
            point.X, point.Y, ClickIntent.InteractUi, "level exact Chance to Bleed row",
            () => IsCommittedState(context.Snapshot(), location), 4_000, cancellationToken);
        if (click != ActionOutcome.Confirmed)
            return LiveTestCaseResult.Fail("Chance to Bleed level increment and panel closure were not proven", "LevelIncrementMismatch");
        if (!await context.WaitForInputIdleAsync("after final gem level-up", 1_500, cancellationToken))
            return LiveTestCaseResult.Fail("input did not settle after final gem level-up", "InputSettleFailed");

        var after = context.Snapshot();
        var upgraded = FindSocketedGems(after).Where(x => x.HolderIndex == location.HolderIndex
            && x.SocketIndex == location.SocketIndex && x.Gem.MetadataPath == TargetPath && x.Gem.Level == 2).ToArray();
        var afterPanel = GemLevelUpView.Read(after.Reader, after.IngameStateAddress);
        context.Check(upgraded.Length == 1, "exact socketed gem incremented",
            $"holder={location.HolderIndex} socket={location.SocketIndex} matches={upgraded.Length}");
        context.Check(!afterPanel.IsVisible && afterPanel.Rows.Count == 0,
            "gem level-up panel disappeared", $"visible={afterPanel.IsVisible} rows={afterPanel.Rows.Count}");
        if (upgraded.Length == 1)
            context.Observe("final-row entity replacement",
                $"item=0x{(long)location.ItemEntity:X}->0x{(long)upgraded[0].ItemEntity:X} " +
                $"gem=0x{(long)location.Gem.EntityAddress:X}->0x{(long)upgraded[0].Gem.EntityAddress:X}");

        return LiveTestCaseResult.Pass(
            $"leveled Chance to Bleed in holder {location.HolderIndex} socket {location.SocketIndex} from 1 to 2 and proved the empty level-up panel closed",
            "CommittedAndVerified");
    }

    private static LiveTestCaseResult Reconcile(LiveTestContext context, GameSnapshot snapshot, GemLevelUpView panel)
    {
        var targets = FindSocketedGems(snapshot).Where(x => x.Gem.MetadataPath == TargetPath
            && x.Gem.Level == 2 && x.SocketIndex == 1).ToArray();
        var recovered = targets.Length == 1 && !panel.IsVisible && panel.Rows.Count == 0;
        context.Check(recovered, "committed final-row reconciliation",
            $"chanceL2Socket1={targets.Length} panelVisible={panel.IsVisible} rows={panel.Rows.Count}");
        return recovered
            ? LiveTestCaseResult.Pass("reconciled committed Chance to Bleed level 1->2 in socket 1 and empty-panel closure", "CommittedAndVerifiedRecovered")
            : LiveTestCaseResult.Blocked("neither the sole-row fixture nor its exact committed post-state was present", "PreparedStateMismatch");
    }

    private static bool IsCommittedState(GameSnapshot snapshot, SocketedGemLocation location)
        => FindSocketedGems(snapshot).Count(x => x.HolderIndex == location.HolderIndex
                && x.SocketIndex == location.SocketIndex && x.Gem.MetadataPath == TargetPath && x.Gem.Level == 2) == 1
            && !GemLevelUpView.Read(snapshot.Reader, snapshot.IngameStateAddress).IsVisible;

    private static IReadOnlyList<SocketedGemLocation> FindSocketedGems(GameSnapshot snapshot)
    {
        var result = new List<SocketedGemLocation>();
        foreach (var inventory in EquipmentInventoriesView.From(snapshot).ServerInventories)
        foreach (var item in ServerInventoryItemsReader.Read(snapshot.Reader, inventory.Address))
        {
            if (!ItemSocketsReader.TryRead(snapshot.Reader, item.EntityAddress, out var sockets)) continue;
            foreach (var gem in sockets.SocketedGems)
                result.Add(new SocketedGemLocation(inventory.HolderIndex, item.EntityAddress, gem.SocketIndex, gem));
        }
        return result;
    }

    private static bool ResolvesHoverTo(GameSnapshot snapshot, nint target)
    {
        snapshot.Reader.TryReadStruct<nint>(snapshot.IngameStateAddress + KnownOffsets.IngameState.UIHover, out var current);
        for (var depth = 0; depth < 24 && current != 0; depth++)
        {
            if (current == target) return true;
            if (!snapshot.Reader.TryReadStruct<nint>(current + KnownOffsets.Element.Parent, out var parent) || parent == current)
                break;
            current = parent;
        }
        return false;
    }

    private readonly record struct SocketedGemLocation(
        int HolderIndex,
        nint ItemEntity,
        int SocketIndex,
        ItemSocketsReader.SocketedGem Gem);
}
