using BubblesBot.Bot.Input;
using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Claims the two declared campaign-route rewards from the nearest prepared NPC.</summary>
public sealed class RouteRewardPairClaimLiveTest : ILiveTestCase
{
    private const int EscapeVk = 0x1B;
    private const int InventoryVk = 0x49;
    private const int LeftControlVk = 0xA2;
    private static readonly IReadOnlySet<string> AllowedPanels =
        new HashSet<string>(StringComparer.Ordinal) { "NpcDialog" };
    private static readonly Fixture[] Fixtures =
    [
        new("Tarkleigh", "Metadata/NPC/Act1/Tarkleigh", "Glyph Reward", "Sniper's Mark", "Metadata/Items/Gems/SkillGemProjectileWeakness"),
        new("Tarkleigh", "Metadata/NPC/Act1/Tarkleigh", "Glyph Reward 2", "Shield Charge", "Metadata/Items/Gems/SkillGemShieldCharge"),
        new("Nessa", "Metadata/NPC/Act1/Nessa", "Medicine Chest Reward", "Quicksilver Flask", "Metadata/Items/Flasks/FlaskUtility6"),
        new("Nessa", "Metadata/NPC/Act1/Nessa", "Medicine Chest Reward 2", "Momentum Support", "Metadata/Items/Gems/SupportGemOnslaught"),
    ];

    public string Id => "A-02-route-reward-pair-claim";
    public string Name => "Claim nearest NPC's two route rewards";
    public string Description => "Claims the two declared route choices from the nearest prepared NPC and proves exact inventory count deltas, consumed dialog controls, and clean continuation.";
    public string ManualSetup => "On a disposable campaign character, stand beside Tarkleigh or Nessa with that NPC's two declared reward controls unclaimed, at least four free inventory cells, cursor free, and a clean HUD.";
    public LiveTestMutation Mutation => LiveTestMutation.Irreversible;
    public bool DrivesInput => true;
    public IReadOnlySet<string> AllowedBlockingPanels => AllowedPanels;

    public async Task<LiveTestCaseResult> RunAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        if (!await NormalizeAsync(context, cancellationToken))
            return LiveTestCaseResult.Fail("could not establish clean quest UI", "SetupRecoveryFailed");
        var start = context.Snapshot();
        if (start.Cursor.Action != CursorView.CursorAction.Free)
            return LiveTestCaseResult.Blocked("cursor must be free", "CursorOccupied");
        var declaredPaths = Fixtures.Select(x => x.NpcPath).ToHashSet(StringComparer.Ordinal);
        var nearest = start.GroundLabels.Where(x => x.IsLabelVisible && x.IsRectOnScreen
                && declaredPaths.Contains(x.Path))
            .OrderBy(x => x.DistanceToPlayer).FirstOrDefault();
        var fixtures = nearest is null ? [] : Fixtures.Where(x => x.NpcPath == nearest.Path).ToArray();
        context.Check(fixtures.Length == 2, "nearest NPC claim pair",
            $"npc='{nearest?.RenderName}' distance={nearest?.DistanceToPlayer:F0} fixtures={fixtures.Length}");
        if (fixtures.Length != 2)
            return LiveTestCaseResult.Blocked("no declared prepared NPC is safely on-screen", "NpcFixtureMissing");

        foreach (var fixture in fixtures)
        {
            var openFailure = await OpenNpcAsync(context, fixture, cancellationToken);
            if (openFailure is not null) return openFailure;
            var actions = ReadDialog(context).FindExact(fixture.Option)
                .Where(x => x.Rect is { Width: > 0, Height: > 0 }).ToArray();
            if (actions.Length == 0 && CountServerPath(context.Snapshot(), fixture.ExpectedMetadata) >= 1)
            {
                context.Check(true, $"{fixture.Option} committed-state reconciliation",
                    $"control absent and '{fixture.ExpectedBase}' metadata present in server inventory");
                if (!await NormalizeAsync(context, cancellationToken))
                    return LiveTestCaseResult.Fail("could not close reconciled NPC dialog", "RestoreFailed");
                continue;
            }
            context.Check(actions.Length == 1, $"{fixture.Option} unclaimed action", $"matches={actions.Length}");
            if (actions.Length != 1 || actions[0].Rect is not { } actionRect)
                return LiveTestCaseResult.Blocked($"'{fixture.Option}' is not available and could not be reconciled", "QuestOptionMissing");

            var optionPoint = context.Snapshot().Window.ToScreen(actionRect.CenterX, actionRect.CenterY);
            var openReward = await context.VerifiedClickAsync(optionPoint.X, optionPoint.Y, ClickIntent.InteractUi,
                $"open {fixture.Option} choices", () => ReadRewards(context).Choices.Any(x => x.IsVisible),
                3_000, cancellationToken);
            if (openReward != ActionOutcome.Confirmed)
                return LiveTestCaseResult.Fail("reward panel did not open", "RewardPanelUnreadable");
            if (!await context.WaitForInputIdleAsync("after reward panel open", 1_500, cancellationToken))
                return LiveTestCaseResult.Fail("input did not settle", "InputSettleFailed");

            var rewards = ReadRewards(context);
            var candidates = rewards.Choices.Where(x => x.IsVisible
                && string.Equals(x.BaseName, fixture.ExpectedBase, StringComparison.OrdinalIgnoreCase)
                && x.Rect is { Width: > 0, Height: > 0 }).ToArray();
            context.Check(candidates.Length == 1, $"{fixture.Option} exact route choice",
                $"expected='{fixture.ExpectedBase}' matches={candidates.Length} live=[{string.Join(",", rewards.Choices.Where(x => x.IsVisible).Select(x => x.BaseName))}]");
            if (candidates.Length != 1 || candidates[0].Rect is not { } candidateRect)
                return LiveTestCaseResult.Fail("declared route reward did not resolve uniquely", "ExpectedRewardMismatch");
            var target = candidates[0];

            var before = context.Snapshot().Inventory;
            if (!before.IsOpen || before.OccupiedCells > 56)
                return LiveTestCaseResult.Blocked("inventory is closed or lacks the four-cell safety margin", "InventoryFull");
            var beforeCounts = PathCounts(before);
            var beforeExpected = CountPath(before, target.Metadata);
            var beforeServerExpected = CountServerPath(context.Snapshot(), target.Metadata);
            var beforeItems = before.Items.Count;

            var hoverPoint = context.Snapshot().Window.ToScreen(candidateRect.CenterX, candidateRect.CenterY);
            await context.HoverAsync(hoverPoint.X, hoverPoint.Y, 150, cancellationToken);
            var hover = await context.WaitUntilAsync($"{fixture.ExpectedBase} final hover",
                () => ReadRewards(context).HoveredChoice?.Item == target.Item, 1_500, cancellationToken, 20);
            if (!hover) return LiveTestCaseResult.Fail("final reward hover identity failed", "RewardHoverMismatch");
            var live = ReadRewards(context).HoveredChoice;
            context.Check(live is not null && live.Metadata == target.Metadata && live.BaseName == target.BaseName
                    && live.RenderedTooltip != 0 && live.TooltipLines.Any(x => x.Contains(live.BaseName, StringComparison.OrdinalIgnoreCase)),
                "final pre-claim reward identity",
                live is null ? "missing" : $"base='{live.BaseName}' metadata='{live.Metadata}' tooltipLines={live.TooltipLines.Count}");
            if (live?.Rect is not { } liveRect || live.Item != target.Item)
                return LiveTestCaseResult.Fail("reward changed before commit", "PreClickIdentityMismatch");

            var claimPoint = context.Snapshot().Window.ToScreen(liveRect.CenterX, liveRect.CenterY);
            var claim = await context.VerifiedModifierClickAsync(claimPoint.X, claimPoint.Y, [LeftControlVk],
                ClickIntent.InteractUi, $"IRREVERSIBLE Ctrl+click claim '{fixture.ExpectedBase}'",
                () => !ReadRewards(context).IsOpen
                    && CountServerPath(context.Snapshot(), target.Metadata) == beforeServerExpected + 1,
                4_000, cancellationToken);
            if (claim != ActionOutcome.Confirmed)
                return LiveTestCaseResult.Fail("claim lacked exact panel/inventory outcome", "RewardClaimOutcomeMismatch");
            if (!await context.WaitForInputIdleAsync("after irreversible reward claim", 1_500, cancellationToken))
                return LiveTestCaseResult.Fail("input did not settle", "InputSettleFailed");

            if (!context.Snapshot().Inventory.IsOpen)
            {
                var openInventory = await context.VerifiedTapKeyAsync(InventoryVk, ClickIntent.InteractUi,
                    "open inventory for post-claim verification", () => context.Snapshot().Inventory.IsOpen,
                    2_000, cancellationToken);
                if (openInventory != ActionOutcome.Confirmed)
                    return LiveTestCaseResult.Fail("could not open inventory after committed claim", "InventoryOutcomeUnreadable");
                if (!await context.WaitForInputIdleAsync("after inventory verification open", 1_500, cancellationToken))
                    return LiveTestCaseResult.Fail("input did not settle", "InputSettleFailed");
            }
            var after = context.Snapshot().Inventory;
            var afterCounts = PathCounts(after);
            context.Check(after.Items.Count == beforeItems + 1, "inventory item count +1",
                $"before={beforeItems} after={after.Items.Count}");
            context.Check(CountPath(after, target.Metadata) == beforeExpected + 1,
                "exact claimed metadata +1", $"metadata='{target.Metadata}' before={beforeExpected} after={CountPath(after, target.Metadata)}");
            context.Check(OnlyExpectedPathDelta(beforeCounts, afterCounts, target.Metadata),
                "no unrelated inventory path-count delta", Diff(beforeCounts, afterCounts));
            context.Check(context.Snapshot().Cursor.Action == CursorView.CursorAction.Free,
                "cursor free after Ctrl+click claim", context.Snapshot().Cursor.Action.ToString());

            if (!await NormalizeAsync(context, cancellationToken))
                return LiveTestCaseResult.Fail("could not normalize after claim", "RestoreFailed");
            var reopenFailure = await OpenNpcAsync(context, fixture, cancellationToken);
            if (reopenFailure is not null) return reopenFailure;
            var consumed = ReadDialog(context).FindExact(fixture.Option).Count == 0;
            context.Check(consumed, $"{fixture.Option} control consumed", consumed ? "absent" : "still present");
            if (!consumed) return LiveTestCaseResult.Fail("reward control remained after claim", "QuestTransitionMismatch");
            if (!await NormalizeAsync(context, cancellationToken))
                return LiveTestCaseResult.Fail("could not close post-claim dialog", "RestoreFailed");
        }

        return LiveTestCaseResult.Pass(
            $"claimed and verified both declared {fixtures[0].NpcName} route rewards: {string.Join(" + ", fixtures.Select(x => x.ExpectedBase))}",
            "CommittedAndVerified");
    }

    private static async Task<LiveTestCaseResult?> OpenNpcAsync(LiveTestContext context, Fixture fixture, CancellationToken cancellationToken)
    {
        var snapshot = context.Snapshot();
        var npc = snapshot.GroundLabels.SingleOrDefault(x => x.Path == fixture.NpcPath
            && string.Equals(x.RenderName, fixture.NpcName, StringComparison.Ordinal)
            && x.IsLabelVisible && x.IsRectOnScreen);
        if (npc?.LabelRect is not { } rect)
            return LiveTestCaseResult.Blocked($"{fixture.NpcName}'s label is unavailable", "NpcLabelMissing");
        var occluders = snapshot.GroundLabels.Where(x => x.LabelAddress != npc.LabelAddress && x.IsLabelVisible)
            .Select(x => x.LabelRect).Where(x => x is not null).Select(x => x!.Value).ToArray();
        var client = InteractSystem.FindUncoveredPoint(rect, occluders);
        if (client is not { } point)
            return LiveTestCaseResult.Blocked($"{fixture.NpcName}'s label is occluded", "NpcLabelOccluded");
        var screen = snapshot.Window.ToScreen(point.X, point.Y);
        var click = await context.VerifiedClickAsync(screen.X, screen.Y, ClickIntent.InteractWorld,
            $"open verified {fixture.NpcName}", () => ReadDialog(context).IsOpen, 3_000, cancellationToken);
        if (click != ActionOutcome.Confirmed)
            return LiveTestCaseResult.Fail("NPC dialog did not open", "NpcDialogOpenFailed");
        if (!await context.WaitForInputIdleAsync("after NPC open", 1_500, cancellationToken))
            return LiveTestCaseResult.Fail("input did not settle", "InputSettleFailed");
        var exactTitle = ReadDialog(context).FindExact(fixture.NpcName).Count == 1;
        context.Check(exactTitle, $"{fixture.NpcName} dialog identity", exactTitle ? "exact title" : "title mismatch");
        return exactTitle ? null : LiveTestCaseResult.Fail("NPC title mismatch", "NpcDialogMismatch");
    }

    private static async Task<bool> NormalizeAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var reward = ReadRewards(context).IsOpen;
            var dialog = ReadDialog(context).IsOpen;
            var inventory = context.Snapshot().Inventory.IsOpen;
            if (!reward && !dialog && !inventory) return true;
            var signature = $"{reward}:{dialog}:{inventory}";
            var close = await context.VerifiedTapKeyAsync(EscapeVk, ClickIntent.InteractUi, "normalize quest UI",
                () => $"{ReadRewards(context).IsOpen}:{ReadDialog(context).IsOpen}:{context.Snapshot().Inventory.IsOpen}" != signature,
                2_000, cancellationToken);
            if (close != ActionOutcome.Confirmed) return false;
            if (!await context.WaitForInputIdleAsync("after quest UI normalization", 1_500, cancellationToken)) return false;
        }
        return !ReadRewards(context).IsOpen && !ReadDialog(context).IsOpen && !context.Snapshot().Inventory.IsOpen;
    }

    private static Dictionary<string, int> PathCounts(InventoryView inventory)
        => inventory.Items.GroupBy(x => x.Path, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);

    private static int CountPath(InventoryView inventory, string path)
        => inventory.Items.Count(x => x.Path == path);

    private static int CountServerPath(GameSnapshot snapshot, string path)
    {
        var count = 0;
        foreach (var inventory in EquipmentInventoriesView.From(snapshot).ServerInventories)
        foreach (var item in ServerInventoryItemsReader.Read(snapshot.Reader, inventory.Address))
            if (string.Equals(EntityListReader.ReadEntityPath(snapshot.Reader, item.EntityAddress), path, StringComparison.Ordinal))
                count++;
        return count;
    }

    private static bool OnlyExpectedPathDelta(
        IReadOnlyDictionary<string, int> before,
        IReadOnlyDictionary<string, int> after,
        string expected)
    {
        var keys = before.Keys.Concat(after.Keys).Distinct(StringComparer.Ordinal);
        return keys.All(key => after.GetValueOrDefault(key) - before.GetValueOrDefault(key) == (key == expected ? 1 : 0));
    }

    private static string Diff(IReadOnlyDictionary<string, int> before, IReadOnlyDictionary<string, int> after)
        => string.Join(", ", before.Keys.Concat(after.Keys).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal)
            .Select(key => $"{key}:{before.GetValueOrDefault(key)}->{after.GetValueOrDefault(key)}")
            .Where(x => !x.EndsWith(":0->0", StringComparison.Ordinal)));

    private static NpcDialogView ReadDialog(LiveTestContext context)
    {
        var snapshot = context.Snapshot();
        return NpcDialogView.Read(snapshot.Reader, snapshot.IngameStateAddress);
    }

    private static QuestRewardView ReadRewards(LiveTestContext context)
    {
        var snapshot = context.Snapshot();
        return QuestRewardView.Read(snapshot.Reader, snapshot.IngameStateAddress);
    }

    private sealed record Fixture(
        string NpcName,
        string NpcPath,
        string Option,
        string ExpectedBase,
        string ExpectedMetadata);
}
