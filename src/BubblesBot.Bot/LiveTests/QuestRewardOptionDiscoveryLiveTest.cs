using BubblesBot.Bot.Input;
using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Opens four declared reward panels, validates every choice and the route-selected item, and closes unclaimed.</summary>
public sealed class QuestRewardOptionDiscoveryLiveTest : ILiveTestCase
{
    private const int EscapeVk = 0x1B;
    private static readonly IReadOnlySet<string> AllowedPanels =
        new HashSet<string>(StringComparer.Ordinal) { "NpcDialog" };
    private static readonly Fixture[] Fixtures =
    [
        new("Tarkleigh", "Metadata/NPC/Act1/Tarkleigh", "Glyph Reward", "Sniper's Mark"),
        new("Tarkleigh", "Metadata/NPC/Act1/Tarkleigh", "Glyph Reward 2", "Shield Charge"),
        new("Nessa", "Metadata/NPC/Act1/Nessa", "Medicine Chest Reward", "Quicksilver Flask"),
        new("Nessa", "Metadata/NPC/Act1/Nessa", "Medicine Chest Reward 2", "Momentum Support"),
    ];

    public string Id => "U-05-fixed-reward-discovery";
    public string Name => "Available quest-reward choice panels";
    public string Description => "For the declared NPC currently on-screen, opens each reward action, proves every choice and the route-selected item through entity/tooltip identity, fingerprints inventory against direct grants, and closes without claiming.";
    public string ManualSetup => "In Lioneye's Watch, keep the declared rewards unclaimed and stand with either Tarkleigh or Nessa visibly on-screen at a clean HUD. Run once beside each NPC.";
    public LiveTestMutation Mutation => LiveTestMutation.Reversible;
    public bool DrivesInput => true;
    public IReadOnlySet<string> AllowedBlockingPanels => AllowedPanels;

    public async Task<LiveTestCaseResult> RunAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        if (!await NormalizeAsync(context, cancellationToken))
            return LiveTestCaseResult.Fail("could not establish a clean quest UI baseline", "SetupRecoveryFailed");
        var initialInventory = ServerInventoryFingerprint(context.Snapshot());
        var start = context.Snapshot();
        var declaredPaths = Fixtures.Select(x => x.NpcPath).ToHashSet(StringComparer.Ordinal);
        var nearestNpc = start.GroundLabels.Where(x => x.IsLabelVisible && x.IsRectOnScreen
                && declaredPaths.Contains(x.Path))
            .OrderBy(x => x.DistanceToPlayer).FirstOrDefault();
        var fixtures = nearestNpc is null
            ? []
            : Fixtures.Where(x => x.NpcPath == nearestNpc.Path).ToArray();
        var npcNames = fixtures.Select(x => x.NpcName).Distinct(StringComparer.Ordinal).ToArray();
        context.Check(fixtures.Length == 2 && npcNames.Length == 1, "nearest declared NPC reward pair",
            $"fixtures={fixtures.Length} npcs=[{string.Join(",", npcNames)}] distance={nearestNpc?.DistanceToPlayer:F0}");
        if (fixtures.Length != 2 || npcNames.Length != 1)
            return LiveTestCaseResult.Blocked("no declared NPC reward pair is safely on-screen", "NpcFixtureMissing");

        foreach (var fixture in fixtures)
        {
            var opened = await OpenNpcAsync(context, fixture, cancellationToken);
            if (opened is not null) return opened;
            var dialog = ReadDialog(context);
            var actions = dialog.FindExact(fixture.Option)
                .Where(x => x.Rect is { Width: > 0, Height: > 0 }).ToArray();
            context.Check(actions.Length == 1, $"{fixture.Option} exact action",
                $"npc='{fixture.NpcName}' matches={actions.Length}");
            if (actions.Length != 1 || actions[0].Rect is not { } actionRect)
            {
                await NormalizeAsync(context, cancellationToken);
                return LiveTestCaseResult.Blocked($"exact reward action '{fixture.Option}' is unavailable", "QuestOptionMissing");
            }

            var beforeInventory = ServerInventoryFingerprint(context.Snapshot());
            var point = context.Snapshot().Window.ToScreen(actionRect.CenterX, actionRect.CenterY);
            var select = await context.VerifiedClickAsync(point.X, point.Y, ClickIntent.InteractUi,
                $"open unclaimed {fixture.Option}",
                () => ReadRewards(context).Choices.Any(x => x.IsVisible)
                    || ServerInventoryFingerprint(context.Snapshot()) != beforeInventory,
                3_000, cancellationToken);
            if (select != ActionOutcome.Confirmed)
            {
                await NormalizeAsync(context, cancellationToken);
                return LiveTestCaseResult.Fail($"'{fixture.Option}' produced neither a readable reward panel nor an inventory delta", "RewardPanelUnreadable");
            }
            if (!await context.WaitForInputIdleAsync($"after {fixture.Option}", 1_500, cancellationToken))
                return LiveTestCaseResult.Fail("input did not settle", "InputSettleFailed");

            var afterOpenInventory = ServerInventoryFingerprint(context.Snapshot());
            context.Check(afterOpenInventory == beforeInventory, $"{fixture.Option} did not grant directly",
                afterOpenInventory == beforeInventory ? "server inventory unchanged" : "UNEXPECTED inventory mutation");
            if (afterOpenInventory != beforeInventory)
                return LiveTestCaseResult.Fail($"'{fixture.Option}' granted an item before a claim click", "UnexpectedDirectGrant");

            var rewards = ReadRewards(context);
            var visible = rewards.Choices.Where(x => x.IsVisible && x.Rect is { Width: > 0, Height: > 0 }).ToArray();
            context.Check(visible.Length >= 1, $"{fixture.Option} readable reward choices",
                $"count={visible.Length} live=[{string.Join(", ", visible.Select(x => x.BaseName))}]");
            var exact = visible.Where(x => string.Equals(x.BaseName, fixture.ExpectedBase, StringComparison.OrdinalIgnoreCase)).ToArray();
            context.Check(exact.Length == 1, $"{fixture.Option} declared identity",
                $"expected='{fixture.ExpectedBase}' matches={exact.Length} metadata=[{string.Join(",", visible.Select(x => x.Metadata))}]");
            if (visible.Length < 1 || exact.Length != 1 || exact[0].Rect is not { })
            {
                await NormalizeAsync(context, cancellationToken);
                return LiveTestCaseResult.Fail($"'{fixture.Option}' did not contain one exact expected route reward '{fixture.ExpectedBase}'", "RewardIdentityMismatch");
            }

            foreach (var choice in visible.OrderBy(x => x.TreePath, StringComparer.Ordinal))
            {
                if (choice.Rect is not { } rewardRect) continue;
                var rewardPoint = context.Snapshot().Window.ToScreen(rewardRect.CenterX, rewardRect.CenterY);
                await context.HoverAsync(rewardPoint.X, rewardPoint.Y, 150, cancellationToken);
                var hover = await context.WaitUntilAsync($"{choice.BaseName} exact reward hover",
                    () => ReadRewards(context).HoveredChoice?.Item == choice.Item,
                    1_500, cancellationToken, 20);
                if (!hover)
                {
                    await NormalizeAsync(context, cancellationToken);
                    return LiveTestCaseResult.Fail($"'{choice.BaseName}' did not resolve through UIHover", "RewardHoverMismatch");
                }
                var current = ReadRewards(context).HoveredChoice;
                context.Check(current is not null && current.RenderedTooltip != 0
                        && current.TooltipLines.Any(x => x.Contains(choice.BaseName, StringComparison.OrdinalIgnoreCase)),
                    $"{choice.BaseName} tooltip identity",
                    current is null ? "missing" : $"metadata='{current.Metadata}' lines=[{string.Join(" | ", current.TooltipLines.Take(12))}]");
            }

            if (!await NormalizeAsync(context, cancellationToken))
                return LiveTestCaseResult.Fail($"could not close unclaimed '{fixture.Option}' UI", "RestoreFailed");
            context.Check(ServerInventoryFingerprint(context.Snapshot()) == beforeInventory,
                $"{fixture.Option} inventory restored", "no reward claimed");
        }

        context.Check(ServerInventoryFingerprint(context.Snapshot()) == initialInventory,
            "all reward probes preserved inventory", "initial server-inventory fingerprint restored");
        return LiveTestCaseResult.Pass(
            $"opened, identified, hovered, and closed both {npcNames[0]} reward choice panels without claiming",
            "CompletedAndRestored");
    }

    private static async Task<LiveTestCaseResult?> OpenNpcAsync(
        LiveTestContext context,
        Fixture fixture,
        CancellationToken cancellationToken)
    {
        var snapshot = context.Snapshot();
        var label = snapshot.GroundLabels.SingleOrDefault(x => x.Path == fixture.NpcPath
            && string.Equals(x.RenderName, fixture.NpcName, StringComparison.Ordinal)
            && x.IsLabelVisible && x.IsRectOnScreen);
        if (label?.LabelRect is not { } rect)
            return LiveTestCaseResult.Blocked($"{fixture.NpcName}'s exact visible label is unavailable", "NpcLabelMissing");
        var occluders = snapshot.GroundLabels.Where(x => x.LabelAddress != label.LabelAddress && x.IsLabelVisible)
            .Select(x => x.LabelRect).Where(x => x is not null).Select(x => x!.Value).ToArray();
        var client = InteractSystem.FindUncoveredPoint(rect, occluders);
        if (client is not { } point)
            return LiveTestCaseResult.Blocked($"{fixture.NpcName}'s label is occluded", "NpcLabelOccluded");
        var screen = snapshot.Window.ToScreen(point.X, point.Y);
        var click = await context.VerifiedClickAsync(screen.X, screen.Y, ClickIntent.InteractWorld,
            $"open verified {fixture.NpcName} dialog for {fixture.Option}",
            () => ReadDialog(context).IsOpen, 3_000, cancellationToken);
        if (click != ActionOutcome.Confirmed)
            return LiveTestCaseResult.Fail($"{fixture.NpcName} dialog did not open", "NpcDialogOpenFailed");
        if (!await context.WaitForInputIdleAsync($"after {fixture.NpcName} open", 1_500, cancellationToken))
            return LiveTestCaseResult.Fail("input did not settle", "InputSettleFailed");
        var title = ReadDialog(context).FindExact(fixture.NpcName).Count;
        context.Check(title == 1, $"{fixture.NpcName} dialog identity", $"titleMatches={title}");
        return title == 1 ? null : LiveTestCaseResult.Fail("NPC dialog identity mismatch", "NpcDialogMismatch");
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
            var close = await context.VerifiedTapKeyAsync(EscapeVk, ClickIntent.InteractUi,
                "close unclaimed quest UI",
                () =>
                {
                    var next = $"{ReadRewards(context).IsOpen}:{ReadDialog(context).IsOpen}:{context.Snapshot().Inventory.IsOpen}";
                    return next != signature;
                }, 2_000, cancellationToken);
            if (close != ActionOutcome.Confirmed) return false;
            if (!await context.WaitForInputIdleAsync("after quest UI close", 1_500, cancellationToken)) return false;
        }
        return !ReadRewards(context).IsOpen && !ReadDialog(context).IsOpen && !context.Snapshot().Inventory.IsOpen;
    }

    private static string ServerInventoryFingerprint(GameSnapshot snapshot)
    {
        var entries = new List<string>();
        foreach (var inventory in EquipmentInventoriesView.From(snapshot).ServerInventories)
        foreach (var item in ServerInventoryItemsReader.Read(snapshot.Reader, inventory.Address))
        {
            var path = EntityListReader.ReadEntityPath(snapshot.Reader, item.EntityAddress) ?? string.Empty;
            var components = EntityComponents.ReadComponentMap(snapshot.Reader, item.EntityAddress);
            var stack = 1;
            if (components.TryGetValue("Stack", out var stackComponent))
                snapshot.Reader.TryReadStruct<int>(stackComponent + KnownOffsets.StackComponent.CurrentCount, out stack);
            var stats = string.Join(',', ItemStatsReader.Read(snapshot.Reader, item.EntityAddress)
                .OrderBy(x => x.Id).Select(x => $"{x.Id}:{x.Value}"));
            var sockets = ItemSocketsReader.TryRead(snapshot.Reader, item.EntityAddress, out var socketState)
                ? socketState.Canonical
                : string.Empty;
            entries.Add($"{inventory.HolderIndex}:{item.MinX},{item.MinY}-{item.MaxX},{item.MaxY}:" +
                $"{path}:stack={stack}:stats={stats}:sockets={sockets}");
        }
        return string.Join('|', entries.OrderBy(x => x, StringComparer.Ordinal));
    }

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

    private sealed record Fixture(string NpcName, string NpcPath, string Option, string ExpectedBase);
}
