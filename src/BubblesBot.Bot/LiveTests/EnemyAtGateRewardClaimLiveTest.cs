using BubblesBot.Bot.Input;
using BubblesBot.Bot.Systems;
using BubblesBot.Core;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Irreversibly claim one explicitly declared Enemy at the Gate reward.</summary>
public sealed class EnemyAtGateRewardClaimLiveTest : ILiveTestCase
{
    private const int EscapeVk = 0x1B;
    private const int InventoryVk = 0x49;
    private const int ToggleLabelsVk = 0x5A;
    private const int LeftControlVk = 0xA2;
    private const int InventoryCells = 60;
    private const string TarkleighPath = "Metadata/NPC/Act1/Tarkleigh";
    private const string RewardOption = "Hillock Reward";
    private static readonly IReadOnlySet<string> AllowedPanels =
        new HashSet<string>(StringComparer.Ordinal) { "NpcDialog" };

    public string Id => "A-02-enemy-at-gate-reward-claim";
    public string Name => "Enemy at the Gate reward claim";
    public string Description => "Claims one explicitly named live reward, proves the exact inventory delta and consumed quest option, classifies continuation, and restores a clean HUD.";
    public string ManualSetup => "On a disposable character with Enemy at the Gate completed but its Tarkleigh reward unclaimed, stand within visible label range of Tarkleigh with at least one free inventory cell and all panels closed. Supply --expect-reward with one exact live base name.";
    public LiveTestMutation Mutation => LiveTestMutation.Irreversible;
    public bool DrivesInput => true;
    public bool RequiresExpectedReward => true;
    public IReadOnlySet<string> AllowedBlockingPanels => AllowedPanels;

    public async Task<LiveTestCaseResult> RunAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        var expected = context.ExpectedReward!;
        if (!ReadRewards(context).IsOpen)
        {
            if (ReadDialog(context).IsOpen)
                return LiveTestCaseResult.Blocked("claim requires either a clean HUD or the already-validated reward panel", "PreparedStateMismatch");
            if (!await OpenTarkleighAsync(context, cancellationToken))
                return LiveTestCaseResult.Fail("verified Tarkleigh dialog did not open", "NpcDialogOpenFailed");
            var dialog = ReadDialog(context);
            var option = dialog.FindExact(RewardOption)
                .Where(x => x.Rect is { Width: > 0, Height: > 0 }).ToArray();
            if (option.Length != 1 || option[0].Rect is not { } optionRect)
            {
                context.Observe("quest reward control", $"exact label='{RewardOption}' matches={option.Length}; entering consumed-checkpoint reconciliation");
                var recovered = await VerifyRecoveredCommittedClaimAsync(context, expected, cancellationToken);
                if (!await NormalizeHudAsync(context, cancellationToken))
                    return LiveTestCaseResult.Fail("could not restore clean HUD after recovered claim", "RestoreFailed");
                return recovered
                    ? LiveTestCaseResult.Pass(
                        $"reconciled claimed reward '{expected}' from prior same-character zero-count baseline, exact inventory entity, and consumed quest control",
                        "CommittedAndVerifiedRecovered")
                    : LiveTestCaseResult.Blocked("Hillock Reward is unavailable without a uniquely matching inventory reward", "RewardAlreadyClaimedOrUnavailable");
            }
            context.Check(true, "unclaimed quest option",
                $"exact label='{RewardOption}' matches=1");

            var optionPoint = context.Snapshot().Window.ToScreen(optionRect.CenterX, optionRect.CenterY);
            var open = await context.VerifiedClickAsync(
                optionPoint.X, optionPoint.Y, ClickIntent.InteractUi, "open Enemy at the Gate reward choices",
                () => ReadRewards(context).IsOpen, 3_000, cancellationToken);
            if (open != ActionOutcome.Confirmed)
                return LiveTestCaseResult.Fail("Hillock Reward did not open Select One Reward", "RewardPanelUnreadable");
            if (!await context.WaitForInputIdleAsync("after reward panel open", 1_500, cancellationToken))
                return LiveTestCaseResult.Fail("input did not settle after reward panel open", "InputSettleFailed");
        }
        else
            context.Observe("prepared-state recovery", "resuming the still-open, previously read-only-verified Select One Reward panel after a no-op click");

        var rewardView = ReadRewards(context);
        var candidates = rewardView.Choices.Where(x => x.IsVisible
            && string.Equals(x.BaseName, expected, StringComparison.OrdinalIgnoreCase)
            && x.Rect is { Width: > 0, Height: > 0 }).ToArray();
        context.Check(candidates.Length == 1, "declared reward resolves uniquely",
            $"expected='{expected}' matches={candidates.Length}; live=[{string.Join(", ", rewardView.Choices.Where(x => x.IsVisible).Select(x => x.BaseName))}]");
        if (candidates.Length != 1 || candidates[0].Rect is not { } rewardRect)
        {
            await NormalizeHudAsync(context, cancellationToken);
            return LiveTestCaseResult.Blocked("declared reward did not resolve to one live choice", "ExpectedRewardMismatch");
        }
        var target = candidates[0];
        context.Check(target.Metadata.StartsWith("Metadata/Items/Gems/", StringComparison.Ordinal),
            "declared reward entity",
            $"base='{target.BaseName}' metadata='{target.Metadata}'");

        var inventory = ReadInventory(context);
        context.Check(inventory.IsOpen, "inventory opened with reward panel", $"items={inventory.Items.Count}");
        context.Check(inventory.OccupiedCells < InventoryCells, "inventory has room for one-cell gem",
            $"occupied={inventory.OccupiedCells}/{InventoryCells}");
        if (!inventory.IsOpen || inventory.OccupiedCells >= InventoryCells)
        {
            await NormalizeHudAsync(context, cancellationToken);
            return LiveTestCaseResult.Blocked("inventory was unreadable or full", "InventoryFull");
        }
        var beforeCount = CountPath(inventory, target.Metadata);
        var beforeFingerprint = InventoryFingerprint(inventory);
        context.Observe("inventory before claim", beforeFingerprint);

        var hover = context.Snapshot().Window.ToScreen(rewardRect.CenterX, rewardRect.CenterY);
        await context.HoverAsync(hover.X, hover.Y, 120, cancellationToken);
        var hovered = await context.WaitUntilAsync(
            "declared reward hover", () => ReadRewards(context).HoveredChoice?.Item == target.Item,
            1_500, cancellationToken, 20);
        if (!hovered)
        {
            await NormalizeHudAsync(context, cancellationToken);
            return LiveTestCaseResult.Fail("declared reward did not resolve through UIHover", "TooltipMismatch");
        }
        var liveTarget = ReadRewards(context).HoveredChoice;
        context.Check(liveTarget?.Metadata == target.Metadata && liveTarget.BaseName == target.BaseName,
            "final pre-click reward identity",
            $"base='{liveTarget?.BaseName}' metadata='{liveTarget?.Metadata}'");
        context.Check(liveTarget is not null && liveTarget.RenderedTooltip != 0
                && liveTarget.TooltipLines.Any(x => x.Contains(liveTarget.BaseName, StringComparison.OrdinalIgnoreCase)),
            "final pre-click rendered tooltip",
            liveTarget is null ? "missing" : $"root=0x{(long)liveTarget.RenderedTooltip:X} lines={liveTarget.TooltipLines.Count}");
        if (liveTarget?.Rect is not { } liveRect || liveTarget.Item != target.Item)
        {
            await NormalizeHudAsync(context, cancellationToken);
            return LiveTestCaseResult.Fail("reward identity changed before commit click", "TooltipMismatch");
        }

        var commitPoint = context.Snapshot().Window.ToScreen(liveRect.CenterX, liveRect.CenterY);
        var claim = await context.VerifiedModifierClickAsync(
            commitPoint.X, commitPoint.Y,
            [LeftControlVk], ClickIntent.InteractUi,
            $"IRREVERSIBLE Ctrl+click claim '{target.BaseName}'",
            () => !ReadRewards(context).IsOpen && CountPath(ReadInventory(context), target.Metadata) == beforeCount + 1,
            4_000, cancellationToken);
        if (claim != ActionOutcome.Confirmed)
            return LiveTestCaseResult.Fail("claim click lacked exact panel/inventory outcome", "RewardClaimOutcomeMismatch");
        if (!await context.WaitForInputIdleAsync("after irreversible reward claim", 1_500, cancellationToken))
            return LiveTestCaseResult.Fail("input did not settle after reward claim", "InputSettleFailed");

        if (!ReadInventory(context).IsOpen)
        {
            var inventoryOpen = await context.VerifiedTapKeyAsync(
                InventoryVk, ClickIntent.InteractUi, "open inventory to verify claimed reward",
                () => ReadInventory(context).IsOpen, 2_000, cancellationToken);
            if (inventoryOpen != ActionOutcome.Confirmed)
                return LiveTestCaseResult.Fail("could not reopen inventory after claim", "InventoryOutcomeUnreadable");
        }
        var after = ReadInventory(context);
        context.Check(CountPath(after, target.Metadata) == beforeCount + 1,
            "exact claimed item inventory delta",
            $"metadata='{target.Metadata}' before={beforeCount} after={CountPath(after, target.Metadata)}");
        context.Check(!string.Equals(InventoryFingerprint(after), beforeFingerprint, StringComparison.Ordinal),
            "inventory fingerprint changed", InventoryFingerprint(after));
        context.Check(!ReadRewards(context).IsOpen, "reward panel continuation", "Select One Reward closed");
        context.Observe("post-claim open panels", string.Join(", ", context.Snapshot().OpenPanels.Open));

        if (!await NormalizeHudAsync(context, cancellationToken))
            return LiveTestCaseResult.Fail("could not restore clean HUD after claim", "RestoreFailed");
        if (!await OpenTarkleighAsync(context, cancellationToken))
            return LiveTestCaseResult.Fail("could not reopen Tarkleigh to verify quest transition", "QuestTransitionUnreadable");
        var afterDialog = ReadDialog(context);
        context.Check(afterDialog.FindExact(RewardOption).Count == 0,
            "quest reward control consumed", $"'{RewardOption}' absent after claim");
        if (afterDialog.FindExact(RewardOption).Count != 0)
            return LiveTestCaseResult.Fail("Hillock Reward remained available after inventory mutation", "QuestTransitionMismatch");
        if (!await NormalizeHudAsync(context, cancellationToken))
            return LiveTestCaseResult.Fail("could not close post-claim Tarkleigh dialog", "RestoreFailed");

        return LiveTestCaseResult.Pass(
            $"claimed exact reward '{target.BaseName}', proved +1 inventory entity and consumed quest control, then restored clean HUD",
            "CommittedAndVerified");
    }

    private static async Task<bool> OpenTarkleighAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        if (ReadDialog(context).IsOpen) return true;
        var snapshot = context.Snapshot();
        var npc = FindTarkleigh(snapshot, requireVisible: false);
        if (npc is not null && npc.IsRectOnScreen && !npc.IsLabelVisible)
        {
            context.Observe("NPC label recovery",
                $"exact Tarkleigh is on-screen at distance={npc.DistanceToPlayer:F0} but labels are hidden; toggling Z");
            var labels = await context.VerifiedTapKeyAsync(
                ToggleLabelsVk, ClickIntent.InteractUi, "enable hidden world labels for Tarkleigh",
                () => FindTarkleigh(context.Snapshot(), requireVisible: true) is not null,
                2_000, cancellationToken);
            if (labels != ActionOutcome.Confirmed) return false;
            if (!await context.WaitForInputIdleAsync("after world-label toggle", 1_500, cancellationToken))
                return false;
            snapshot = context.Snapshot();
            npc = FindTarkleigh(snapshot, requireVisible: true);
        }
        if (npc?.LabelRect is not { } rect) return false;
        var occluders = snapshot.GroundLabels.Where(x => x.LabelAddress != npc.LabelAddress && x.IsLabelVisible)
            .Select(x => x.LabelRect).Where(x => x is not null).Select(x => x!.Value).ToArray();
        var point = InteractSystem.FindUncoveredPoint(rect, occluders);
        if (point is not { } p) return false;
        context.Check(true, "Tarkleigh exact world identity",
            $"path='{npc.Path}' name='{npc.RenderName}' distance={npc.DistanceToPlayer:F0}");
        var screen = snapshot.Window.ToScreen(p.X, p.Y);
        var click = await context.VerifiedClickAsync(
            screen.X, screen.Y, ClickIntent.InteractWorld, "open verified Tarkleigh dialog",
            () => ReadDialog(context).IsOpen, 3_000, cancellationToken);
        if (click != ActionOutcome.Confirmed) return false;
        return await context.WaitForInputIdleAsync("after Tarkleigh click", 1_500, cancellationToken);
    }

    private static GroundLabelView? FindTarkleigh(GameSnapshot snapshot, bool requireVisible)
        => snapshot.GroundLabels.SingleOrDefault(x => x.Path == TarkleighPath
            && string.Equals(x.RenderName, "Tarkleigh", StringComparison.Ordinal)
            && x.IsRectOnScreen
            && (!requireVisible || x.IsLabelVisible));

    private static async Task<bool> NormalizeHudAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var rewards = ReadRewards(context).IsOpen;
            var dialog = ReadDialog(context).IsOpen;
            var inventory = ReadInventory(context).IsOpen;
            if (!rewards && !dialog && !inventory) return true;
            var signature = $"{rewards}:{dialog}:{inventory}";
            var close = await context.VerifiedTapKeyAsync(
                EscapeVk, ClickIntent.InteractUi, "normalize quest/inventory UI",
                () =>
                {
                    var next = $"{ReadRewards(context).IsOpen}:{ReadDialog(context).IsOpen}:{ReadInventory(context).IsOpen}";
                    return !string.Equals(next, signature, StringComparison.Ordinal);
                }, 2_000, cancellationToken);
            if (close != ActionOutcome.Confirmed) return false;
            if (!await context.WaitForInputIdleAsync("after UI normalization step", 1_500, cancellationToken))
                return false;
        }
        return !ReadRewards(context).IsOpen && !ReadDialog(context).IsOpen && !ReadInventory(context).IsOpen;
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

    private static InventoryView ReadInventory(LiveTestContext context) => context.Snapshot().Inventory;

    private static async Task<bool> VerifyRecoveredCommittedClaimAsync(
        LiveTestContext context,
        string expected,
        CancellationToken cancellationToken)
    {
        if (!ReadInventory(context).IsOpen)
        {
            var opened = await context.VerifiedTapKeyAsync(
                InventoryVk, ClickIntent.InteractUi, "open inventory for consumed-checkpoint reconciliation",
                () => ReadInventory(context).IsOpen, 2_000, cancellationToken);
            if (opened != ActionOutcome.Confirmed) return false;
            if (!await context.WaitForInputIdleAsync("after reconciliation inventory open", 1_500, cancellationToken))
                return false;
        }
        var snapshot = context.Snapshot();
        var inventory = snapshot.Inventory;
        var matches = inventory.Items
            .Select(x => new { Item = x, BaseName = ReadBaseName(snapshot.Reader, x.ItemEntity) })
            .Where(x => string.Equals(x.BaseName, expected, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        context.Check(inventory.IsOpen, "recovery inventory readable", $"items={inventory.Items.Count}");
        context.Check(matches.Length == 1, "recovered exact claimed reward",
            matches.Length == 1
                ? $"base='{matches[0].BaseName}' metadata='{matches[0].Item.Path}' item=0x{(long)matches[0].Item.ItemEntity:X}"
                : $"expected='{expected}' matches={matches.Length}");
        context.Observe("recovery evidence join",
            "valid only with the retained same-character pre-claim inventory fingerprint showing zero matching rewards; cursor-held-item reading remains unpromoted");
        return inventory.IsOpen && matches.Length == 1;
    }

    private static string ReadBaseName(MemoryReader reader, nint itemEntity)
    {
        var components = EntityComponents.ReadComponentMap(reader, itemEntity);
        if (!components.TryGetValue("Base", out var component)
            || !reader.TryReadStruct<nint>(component + KnownOffsets.BaseComponent.ItemInfo, out var info)
            || info == 0)
            return string.Empty;
        return NativeString.Read(reader, info + KnownOffsets.ItemInfo.BaseName);
    }

    private static int CountPath(InventoryView inventory, string metadata)
        => inventory.Items.Count(x => string.Equals(x.Path, metadata, StringComparison.Ordinal));

    private static string InventoryFingerprint(InventoryView inventory)
        => string.Join('|', inventory.Items.OrderBy(x => x.Path, StringComparer.Ordinal)
            .ThenBy(x => x.Rect?.Y ?? float.MaxValue).ThenBy(x => x.Rect?.X ?? float.MaxValue)
            .Select(x => $"{x.Path}:{x.StackSize}:{x.Width}x{x.Height}:{FormatRect(x.Rect)}"));

    private static string FormatRect(ElementGeometry.Rect? rect)
        => rect is { } r ? $"{r.X:F0},{r.Y:F0},{r.Width:F0},{r.Height:F0}" : "none";
}
