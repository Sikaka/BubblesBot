using BubblesBot.Bot.Input;
using BubblesBot.Bot.Systems;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Open Tarkleigh's prepared Enemy at the Gate reward and inspect it without claiming.</summary>
public sealed class EnemyAtGateRewardDiscoveryLiveTest : ILiveTestCase
{
    private const int EscapeVk = 0x1B;
    private const string TarkleighPath = "Metadata/NPC/Act1/Tarkleigh";
    // The quest tracker/player-facing quest is "Enemy at the Gate", while Tarkleigh's
    // validated actionable dialog label is "Hillock Reward" (observed 2026-07-16).
    private const string RewardOption = "Hillock Reward";
    private static readonly IReadOnlySet<string> AllowedPanels =
        new HashSet<string>(StringComparer.Ordinal) { "NpcDialog" };

    public string Id => "U-05-enemy-at-gate-reward-discovery";
    public string Name => "Enemy at the Gate reward discovery";
    public string Description => "Opens verified Tarkleigh and the exact Hillock Reward control for Enemy at the Gate, discovers live reward items, validates every hover tooltip, then closes without claiming.";
    public string ManualSetup => "On a character with Enemy at the Gate completed but its Tarkleigh reward unclaimed, stand within visible label range of Tarkleigh in Lioneye's Watch with all panels closed and PoE focused.";
    public LiveTestMutation Mutation => LiveTestMutation.Reversible;
    public bool DrivesInput => true;
    public IReadOnlySet<string> AllowedBlockingPanels => AllowedPanels;

    public async Task<LiveTestCaseResult> RunAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        if (ReadRewards(context).IsOpen)
        {
            context.Observe("prepared-state recovery", "an unclaimed Select One Reward panel is already open; closing it before the measured cycle");
            if (!await CloseRewardAsync(context, cancellationToken))
                return LiveTestCaseResult.Fail("could not close the already-open reward panel", "SetupRecoveryFailed");
        }
        if (ReadDialog(context).IsOpen)
        {
            context.Observe("prepared-state recovery", "NPC dialog remained open after reward close; closing it before the measured cycle");
            if (!await CloseDialogAsync(context, cancellationToken))
                return LiveTestCaseResult.Fail("could not close the already-open NPC dialog", "SetupRecoveryFailed");
        }

        var baseline = context.Snapshot();
        var tarkleigh = baseline.GroundLabels.SingleOrDefault(x =>
            x.Path == TarkleighPath
            && string.Equals(x.RenderName, "Tarkleigh", StringComparison.Ordinal)
            && x.IsLabelVisible
            && x.IsRectOnScreen);
        if (tarkleigh?.LabelRect is not { } labelRect)
            return LiveTestCaseResult.Blocked("Tarkleigh's exact visible world label was not readable", "NpcLabelMissing");
        context.Check(tarkleigh.DistanceToPlayer < 60, "Tarkleigh world identity",
            $"path='{tarkleigh.Path}' name='{tarkleigh.RenderName}' distance={tarkleigh.DistanceToPlayer:F0}");

        var occluders = baseline.GroundLabels
            .Where(x => x.LabelAddress != tarkleigh.LabelAddress && x.IsLabelVisible)
            .Select(x => x.LabelRect).Where(x => x is not null).Select(x => x!.Value).ToArray();
        var point = InteractSystem.FindUncoveredPoint(labelRect, occluders);
        context.Check(point is not null, "Tarkleigh label click point",
            point is { } p ? $"client={p.X:F0},{p.Y:F0} occluders={occluders.Length}" : "fully occluded");
        if (point is not { } npcPoint)
            return LiveTestCaseResult.Blocked("Tarkleigh's label is fully occluded", "NpcLabelOccluded");

        var screen = baseline.Window.ToScreen(npcPoint.X, npcPoint.Y);
        var opened = await context.VerifiedClickAsync(
            screen.X, screen.Y, ClickIntent.InteractWorld, "open verified Tarkleigh dialog",
            () => ReadDialog(context).IsOpen, 3_000, cancellationToken);
        if (opened != ActionOutcome.Confirmed)
            return LiveTestCaseResult.Fail("Tarkleigh click did not open NpcDialog", "NpcDialogOpenFailed");
        if (!await context.WaitForInputIdleAsync("after Tarkleigh click", 1_500, cancellationToken))
            return LiveTestCaseResult.Fail("input did not settle after Tarkleigh click", "InputSettleFailed");

        var dialog = ReadDialog(context);
        context.Observe("Tarkleigh dialog controls", string.Join(" | ", dialog.Controls
            .Select(x => $"'{OneLine(x.Text)}'@{x.TreePath}").Take(80)));
        context.Check(dialog.FindExact("Tarkleigh").Count == 1, "Tarkleigh dialog identity",
            $"panel=0x{(long)dialog.Panel:X}");
        var quest = dialog.FindExact(RewardOption)
            .Where(x => x.Rect is { Width: > 0, Height: > 0 })
            .ToArray();
        context.Check(quest.Length == 1, "Enemy at the Gate reward control identity",
            $"exact label='{RewardOption}' matches={quest.Length}");
        if (quest.Length != 1 || quest[0].Rect is not { } questRect)
        {
            await CloseAnyQuestUiAsync(context, cancellationToken);
            return LiveTestCaseResult.Blocked("one exact Hillock Reward dialog control was not available", "QuestOptionMissing");
        }

        var optionPoint = context.Snapshot().Window.ToScreen(questRect.CenterX, questRect.CenterY);
        var selected = await context.VerifiedClickAsync(
            optionPoint.X, optionPoint.Y, ClickIntent.InteractUi, "open Enemy at the Gate reward choices",
            () => ReadRewards(context).Choices.Any(x => x.IsVisible), 3_000, cancellationToken);
        if (selected != ActionOutcome.Confirmed)
        {
            ObserveCurrentUi(context, "reward-open failure");
            await CloseAnyQuestUiAsync(context, cancellationToken);
            return LiveTestCaseResult.Fail("quest option did not expose readable item-backed reward choices", "RewardPanelUnreadable");
        }
        if (!await context.WaitForInputIdleAsync("after quest option", 1_500, cancellationToken))
            return LiveTestCaseResult.Fail("input did not settle after quest option", "InputSettleFailed");

        var rewards = ReadRewards(context);
        var visible = rewards.Choices.Where(x => x.IsVisible && x.Rect is { } r
            && r.IntersectsWindow(baseline.Window.Width, baseline.Window.Height))
            .OrderBy(x => x.TreePath, StringComparer.Ordinal).ToArray();
        ObserveCurrentUi(context, "reward-open");
        context.Observe("reward choices", string.Join(" | ", visible.Select(x =>
            $"{x.BaseName} [{x.Metadata}] @{x.TreePath} {FormatRect(x.Rect)}")));
        context.Check(visible.Length >= 1, "visible reward choices", $"count={visible.Length}");
        context.Check(visible.All(x => !string.IsNullOrWhiteSpace(x.BaseName)
            && x.Metadata.StartsWith("Metadata/Items/Gems/", StringComparison.Ordinal)),
            "reward entity identities", string.Join(", ", visible.Select(x => x.BaseName)));

        for (var i = 0; i < visible.Length; i++)
        {
            var target = visible[i];
            var rect = target.Rect!.Value;
            var hoverPoint = baseline.Window.ToScreen(rect.CenterX, rect.CenterY);
            await context.HoverAsync(hoverPoint.X, hoverPoint.Y, 120, cancellationToken);
            var reached = await context.WaitUntilAsync(
                $"reward hover {i + 1}/{visible.Length}",
                () => ReadRewards(context).HoveredChoice?.Item == target.Item,
                1_500, cancellationToken, 20);
            if (!reached)
            {
                await CloseAnyQuestUiAsync(context, cancellationToken);
                return LiveTestCaseResult.Fail($"UIHover did not resolve reward '{target.BaseName}'", "RewardHoverMismatch");
            }

            var current = ReadRewards(context).HoveredChoice;
            context.Check(current is not null && current.Item == target.Item && current.Element == target.Element,
                $"reward {i + 1}/{visible.Length} exact hover identity",
                $"base='{current?.BaseName}' item=0x{(long)(current?.Item ?? 0):X}");
            context.Check(current is not null && current.RenderedTooltip != 0 && current.TooltipLines.Count > 0,
                $"reward {i + 1}/{visible.Length} rendered tooltip",
                current is null ? "missing" : $"root=0x{(long)current.RenderedTooltip:X} lines={current.TooltipLines.Count}");
            context.Check(current is not null && current.TooltipLines.Any(x =>
                    x.Contains(current.BaseName, StringComparison.OrdinalIgnoreCase)),
                $"reward {i + 1}/{visible.Length} tooltip/base agreement",
                current is null ? "missing" : string.Join(" | ", current.TooltipLines.Take(10)));
        }

        var fingerprint = Fingerprint(visible);
        var beforeClose = ReadRewards(context);
        context.Check(Fingerprint(beforeClose.Choices.Where(x => x.IsVisible)) == fingerprint,
            "reward choices unchanged after hover sweep", fingerprint);
        if (!await CloseAnyQuestUiAsync(context, cancellationToken))
            return LiveTestCaseResult.Fail("the unclaimed reward panel and returned NPC dialog did not close", "RestoreFailed");
        context.Check(!ReadDialog(context).IsOpen && !ReadRewards(context).IsOpen,
            "clean UI baseline restored", "NpcDialog and reward panel closed; no reward clicked");
        return LiveTestCaseResult.Pass(
            $"discovered and hover-validated {visible.Length} live reward choices; closed without claiming",
            "CompletedAndRestored");
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

    private static async Task<bool> CloseDialogAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        if (!ReadDialog(context).IsOpen) return true;
        var close = await context.VerifiedTapKeyAsync(
            EscapeVk, ClickIntent.InteractUi, "close unclaimed quest reward dialog",
            () => !ReadDialog(context).IsOpen, 2_000, cancellationToken);
        if (close != ActionOutcome.Confirmed) return false;
        return await context.WaitForInputIdleAsync("after reward dialog close", 1_500, cancellationToken);
    }

    private static async Task<bool> CloseRewardAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        if (!ReadRewards(context).IsOpen) return true;
        var close = await context.VerifiedTapKeyAsync(
            EscapeVk, ClickIntent.InteractUi, "close unclaimed Select One Reward panel",
            () => !ReadRewards(context).IsOpen, 2_000, cancellationToken);
        if (close != ActionOutcome.Confirmed) return false;
        return await context.WaitForInputIdleAsync("after reward panel close", 1_500, cancellationToken);
    }

    private static async Task<bool> CloseAnyQuestUiAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        if (!await CloseRewardAsync(context, cancellationToken)) return false;
        return await CloseDialogAsync(context, cancellationToken);
    }

    private static void ObserveCurrentUi(LiveTestContext context, string label)
    {
        var snapshot = context.Snapshot();
        var text = VisibleUiTextView.ReadInGame(snapshot.Reader, snapshot.IngameStateAddress);
        context.Observe(label + " visible text", string.Join(" | ", text.Elements
            .Where(x => x.Rect is { } r && r.IntersectsWindow(snapshot.Window.Width, snapshot.Window.Height))
            .Select(x => $"'{OneLine(x.Text)}'@{x.TreePath}").Take(120)));
    }

    private static string Fingerprint(IEnumerable<QuestRewardView.Choice> choices)
        => string.Join('|', choices.OrderBy(x => x.TreePath, StringComparer.Ordinal)
            .Select(x => $"{x.TreePath}:{x.Metadata}:{x.BaseName}:{FormatRect(x.Rect)}"));

    private static string OneLine(string text)
        => string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string FormatRect(ElementGeometry.Rect? rect)
        => rect is { } r ? $"{r.X:F0},{r.Y:F0},{r.Width:F0},{r.Height:F0}" : "none";
}
