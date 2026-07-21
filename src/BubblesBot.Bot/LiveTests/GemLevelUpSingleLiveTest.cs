using BubblesBot.Bot.Input;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>One guarded, irreversible upgrade of the exact Splitting Steel panel row.</summary>
public sealed class GemLevelUpSingleLiveTest : ILiveTestCase
{
    private const string TargetPath = "Metadata/Items/Gems/SkillGemSplittingSteel";
    private const string OtherPath = "Metadata/Items/Gems/SupportGemChanceToBleed";

    public string Id => "U-07-gem-level-up-single";
    public string Name => "Exact gem-row level-up";
    public string Description => "Clicks the live row bound to Splitting Steel's exact entity, then proves its level increment, socket persistence, row removal, and All-control removal.";
    public string ManualSetup => "Have level-1 Splitting Steel and Chance to Bleed socketed and ready to level, with exactly two visible rows and the All control. This consumes Splitting Steel's pending level-up.";
    public LiveTestMutation Mutation => LiveTestMutation.Irreversible;
    public bool DrivesInput => true;

    public async Task<LiveTestCaseResult> RunAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        var beforeSnapshot = context.Snapshot();
        var before = GemLevelUpView.Read(beforeSnapshot.Reader, beforeSnapshot.IngameStateAddress);
        var targetRows = before.Rows.Where(x => x.Gem.MetadataPath == TargetPath).ToArray();
        var otherRows = before.Rows.Where(x => x.Gem.MetadataPath == OtherPath).ToArray();
        var prepared = before.IsVisible && before.Rows.Count == 2 && before.AllControl is not null
            && targetRows.Length == 1 && otherRows.Length == 1 && targetRows[0].Gem.Level == 1;
        if (!prepared)
            return ReconcileCommittedState(context, beforeSnapshot, before);
        context.Check(true, "prepared exact two-gem fixture",
            $"visible={before.IsVisible} rows={before.Rows.Count} all={before.AllControl is not null} " +
            $"splitting={targetRows.Length} chance={otherRows.Length}");

        var target = targetRows[0];
        var beforeLevel = target.Gem.Level;
        var targetEntity = target.GemEntity;
        var targetLocations = FindSocketedGems(beforeSnapshot)
            .Where(x => x.Gem.EntityAddress == targetEntity).ToArray();
        context.Check(targetLocations.Length == 1, "target entity socketed before click",
            $"entity=0x{(long)targetEntity:X} gem='{target.Gem.BaseName}' matches={targetLocations.Length}");
        if (targetLocations.Length != 1)
            return LiveTestCaseResult.Blocked("target row entity did not resolve to one equipped socket", "SocketIdentityMismatch");
        var targetLocation = targetLocations[0];
        context.Check(target.LevelControl.Rect.Width > 0 && target.LevelControl.Rect.Height > 0,
            "target level control geometry", target.LevelControl.Rect.ToString());

        var point = beforeSnapshot.Window.ToScreen(
            target.LevelControl.Rect.CenterX, target.LevelControl.Rect.CenterY);
        await context.HoverAsync(point.X, point.Y, 180, cancellationToken);
        var exactHover = await context.WaitUntilAsync("exact Splitting Steel level control hover",
            () => ResolvesHoverTo(context.Snapshot(), target.LevelControl.Element),
            1_500, cancellationToken, 20);
        if (!exactHover)
            return LiveTestCaseResult.Fail("cursor did not resolve to Splitting Steel's exact level control", "TargetHoverMismatch");

        var finalPreClick = GemLevelUpView.Read(context.Snapshot().Reader, context.Snapshot().IngameStateAddress)
            .Rows.SingleOrDefault(x => x.GemEntity == targetEntity);
        context.Check(finalPreClick?.Gem.MetadataPath == TargetPath && finalPreClick.Gem.Level == beforeLevel,
            "final pre-click entity identity",
            finalPreClick is null ? "missing" : $"entity=0x{(long)finalPreClick.GemEntity:X} gem='{finalPreClick.Gem.BaseName}' level={finalPreClick.Gem.Level}");
        if (finalPreClick is null || finalPreClick.Gem.MetadataPath != TargetPath || finalPreClick.Gem.Level != beforeLevel)
            return LiveTestCaseResult.Fail("target identity changed before dispatch", "PreClickIdentityMismatch");

        var click = await context.VerifiedClickAsync(
            point.X, point.Y, ClickIntent.InteractUi, "level exact Splitting Steel entity",
            () => IsExpectedCommittedState(context.Snapshot(), targetLocation, beforeLevel + 1),
            4_000, cancellationToken);
        if (click != ActionOutcome.Confirmed)
            return LiveTestCaseResult.Fail("Splitting Steel level increment was not proven", "LevelIncrementMismatch");
        if (!await context.WaitForInputIdleAsync("after gem level-up", 1_500, cancellationToken))
            return LiveTestCaseResult.Fail("input did not settle after gem level-up", "InputSettleFailed");

        var afterSnapshot = context.Snapshot();
        var afterTargets = FindSocketedGems(afterSnapshot).Where(x =>
            x.HolderIndex == targetLocation.HolderIndex
            && x.SocketIndex == targetLocation.SocketIndex
            && x.Gem.MetadataPath == TargetPath).ToArray();
        if (afterTargets.Length != 1)
            return LiveTestCaseResult.Fail("upgraded gem state became unreadable", "GemStateUnreadable");
        var afterTarget = afterTargets[0];
        var afterGem = afterTarget.Gem;
        var after = GemLevelUpView.Read(afterSnapshot.Reader, afterSnapshot.IngameStateAddress);
        context.Check(afterGem.Level == beforeLevel + 1, "exact gem level increment",
            $"entity=0x{(long)targetEntity:X}->0x{(long)afterGem.EntityAddress:X} level={beforeLevel}->{afterGem.Level} " +
            $"rawXp={target.Gem.TotalExperience}->{afterGem.TotalExperience} range={afterGem.PreviousLevelExperience}-{afterGem.NextLevelExperience}");
        context.Check(afterTarget.HolderIndex == targetLocation.HolderIndex
                && afterTarget.SocketIndex == targetLocation.SocketIndex,
            "upgraded gem remains in exact socket",
            $"holder={afterTarget.HolderIndex} socket={afterTarget.SocketIndex} entity=0x{(long)afterGem.EntityAddress:X}");
        context.Check(after.Rows.Count == 1 && after.Rows[0].Gem.MetadataPath == OtherPath,
            "only Chance to Bleed row remains",
            $"rows={after.Rows.Count} entities=[{string.Join(",", after.Rows.Select(x => $"0x{(long)x.GemEntity:X}:{x.Gem.BaseName}"))}]");
        context.Check(after.AllControl is null, "All control removed with one pending gem",
            after.AllControl is null ? "absent" : $"element=0x{(long)after.AllControl.Element:X}");
        context.Check(after.Rows.All(x => x.Gem.MetadataPath != TargetPath), "upgraded row consumed",
            $"target='{TargetPath}'");
        context.Observe("level-up entity replacement",
            $"item=0x{(long)targetLocation.ItemEntity:X}->0x{(long)afterTarget.ItemEntity:X} " +
            $"gem=0x{(long)targetEntity:X}->0x{(long)afterGem.EntityAddress:X}; persistent identity is holder/socket/path/state, not pointer");

        return LiveTestCaseResult.Pass(
            $"leveled Splitting Steel in holder {afterTarget.HolderIndex} socket {afterTarget.SocketIndex} from {beforeLevel} to {afterGem.Level}; socket persisted and the panel collapsed to Chance to Bleed without All",
            "CommittedAndVerified");
    }

    private static LiveTestCaseResult ReconcileCommittedState(
        LiveTestContext context,
        GameSnapshot snapshot,
        GemLevelUpView panel)
    {
        var gems = FindSocketedGems(snapshot);
        var target = gems.Where(x => x.Gem.MetadataPath == TargetPath && x.Gem.Level == 2).ToArray();
        var other = gems.Where(x => x.Gem.MetadataPath == OtherPath && x.Gem.Level == 1).ToArray();
        var recovered = target.Length == 1 && other.Length == 1
            && target[0].HolderIndex == other[0].HolderIndex
            && target[0].SocketIndex == 0 && other[0].SocketIndex == 1
            && panel.Rows.Count == 1 && panel.Rows[0].Gem.MetadataPath == OtherPath
            && panel.AllControl is null;
        context.Check(recovered, "committed level-up reconciliation",
            $"splittingL2={target.Length} chanceL1={other.Length} rows={panel.Rows.Count} all={panel.AllControl is not null}");
        if (!recovered)
            return LiveTestCaseResult.Blocked("neither the prepared fixture nor the exact committed post-state was present", "PreparedStateMismatch");
        context.Check(panel.Rows.All(x => x.Gem.MetadataPath != TargetPath), "reconciled upgraded row consumed",
            string.Join(",", panel.Rows.Select(x => x.Gem.MetadataPath)));
        context.Check(IsExpectedCommittedState(snapshot, target[0], 2),
            "reconciled exact socket and level", $"holder={target[0].HolderIndex} socket={target[0].SocketIndex} level={target[0].Gem.Level}");
        return LiveTestCaseResult.Pass(
            $"reconciled committed Splitting Steel level 1->2 in holder {target[0].HolderIndex} socket {target[0].SocketIndex}; Chance to Bleed is the sole remaining row and All is absent",
            "CommittedAndVerifiedRecovered");
    }

    private static bool IsExpectedCommittedState(GameSnapshot snapshot, SocketedGemLocation before, uint expectedLevel)
    {
        var current = FindSocketedGems(snapshot).Where(x =>
            x.HolderIndex == before.HolderIndex
            && x.SocketIndex == before.SocketIndex
            && x.Gem.MetadataPath == before.Gem.MetadataPath
            && x.Gem.Level == expectedLevel).ToArray();
        if (current.Length != 1) return false;
        var panel = GemLevelUpView.Read(snapshot.Reader, snapshot.IngameStateAddress);
        return panel.Rows.All(x => x.Gem.MetadataPath != TargetPath);
    }

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
            if (!snapshot.Reader.TryReadStruct<nint>(current + KnownOffsets.Element.Parent, out var parent)
                || parent == current)
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
