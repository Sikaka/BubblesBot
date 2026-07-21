using BubblesBot.Bot.Input;
using BubblesBot.Bot.Systems;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Read-only-in-outcome catalog of currently available Tarkleigh and Nessa dialog actions.</summary>
public sealed class QuestRewardDialogCatalogLiveTest : ILiveTestCase
{
    private const int EscapeVk = 0x1B;
    private static readonly IReadOnlySet<string> AllowedPanels =
        new HashSet<string>(StringComparer.Ordinal) { "NpcDialog" };
    private static readonly Npc[] Npcs =
    [
        new("Tarkleigh", "Metadata/NPC/Act1/Tarkleigh"),
        new("Nessa", "Metadata/NPC/Act1/Nessa"),
    ];

    public string Id => "U-05-quest-reward-dialog-catalog";
    public string Name => "Available quest-reward dialog catalog";
    public string Description => "Opens Tarkleigh and Nessa by exact world identity, records every live dialog action and geometry, and restores a clean HUD without selecting a reward.";
    public string ManualSetup => "In Lioneye's Watch, stand within visible label range of Tarkleigh and Nessa with all panels closed and the intended quest rewards unclaimed.";
    public LiveTestMutation Mutation => LiveTestMutation.Reversible;
    public bool DrivesInput => true;
    public IReadOnlySet<string> AllowedBlockingPanels => AllowedPanels;

    public async Task<LiveTestCaseResult> RunAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        if (!await CloseDialogAsync(context, "prepared dialog", cancellationToken))
            return LiveTestCaseResult.Fail("could not establish a clean dialog baseline", "SetupRecoveryFailed");

        foreach (var npc in Npcs)
        {
            var snapshot = context.Snapshot();
            var label = snapshot.GroundLabels.SingleOrDefault(x => x.Path == npc.Path
                && string.Equals(x.RenderName, npc.Name, StringComparison.Ordinal)
                && x.IsLabelVisible && x.IsRectOnScreen);
            if (label?.LabelRect is not { } labelRect)
                return LiveTestCaseResult.Blocked($"{npc.Name}'s exact visible world label is unavailable", "NpcLabelMissing");

            var occluders = snapshot.GroundLabels.Where(x => x.LabelAddress != label.LabelAddress && x.IsLabelVisible)
                .Select(x => x.LabelRect).Where(x => x is not null).Select(x => x!.Value).ToArray();
            var client = InteractSystem.FindUncoveredPoint(labelRect, occluders);
            context.Check(client is not null, $"{npc.Name} uncovered label point",
                client is { } p ? $"path='{npc.Path}' distance={label.DistanceToPlayer:F0} client=({p.X:F0},{p.Y:F0})" : "fully occluded");
            if (client is not { } point)
                return LiveTestCaseResult.Blocked($"{npc.Name}'s label is fully occluded", "NpcLabelOccluded");

            var screen = snapshot.Window.ToScreen(point.X, point.Y);
            var open = await context.VerifiedClickAsync(screen.X, screen.Y, ClickIntent.InteractWorld,
                $"open verified {npc.Name} dialog", () => ReadDialog(context).IsOpen, 3_000, cancellationToken);
            if (open != ActionOutcome.Confirmed)
                return LiveTestCaseResult.Fail($"{npc.Name} dialog did not open", "NpcDialogOpenFailed");
            if (!await context.WaitForInputIdleAsync($"after {npc.Name} dialog open", 1_500, cancellationToken))
                return LiveTestCaseResult.Fail("input did not settle", "InputSettleFailed");

            var dialog = ReadDialog(context);
            var titleMatches = dialog.FindExact(npc.Name).Count;
            context.Check(titleMatches == 1, $"{npc.Name} dialog identity",
                $"panel=0x{(long)dialog.Panel:X} titleMatches={titleMatches}");
            var controls = dialog.Controls.Where(x => x.Rect is { Width: > 0, Height: > 0 })
                .Select(x => $"'{OneLine(x.Text)}'@{x.TreePath}:{Format(x.Rect)}:children={x.ChildCount}")
                .Distinct(StringComparer.Ordinal).ToArray();
            context.Observe($"{npc.Name} live dialog catalog", string.Join(" | ", controls));
            context.Check(controls.Length >= 2, $"{npc.Name} readable dialog controls", $"count={controls.Length}");

            if (!await CloseDialogAsync(context, npc.Name + " dialog", cancellationToken))
                return LiveTestCaseResult.Fail($"{npc.Name} dialog did not close", "RestoreFailed");
        }

        context.Check(!ReadDialog(context).IsOpen, "clean dialog baseline restored", "NpcDialog closed; no reward action selected");
        return LiveTestCaseResult.Pass("cataloged current Tarkleigh and Nessa dialog actions and restored a clean HUD", "CompletedAndRestored");
    }

    private static async Task<bool> CloseDialogAsync(
        LiveTestContext context,
        string description,
        CancellationToken cancellationToken)
    {
        if (!ReadDialog(context).IsOpen) return true;
        var close = await context.VerifiedTapKeyAsync(EscapeVk, ClickIntent.InteractUi,
            "close " + description, () => !ReadDialog(context).IsOpen, 2_000, cancellationToken);
        return close == ActionOutcome.Confirmed
            && await context.WaitForInputIdleAsync("after " + description + " close", 1_500, cancellationToken);
    }

    private static NpcDialogView ReadDialog(LiveTestContext context)
    {
        var snapshot = context.Snapshot();
        return NpcDialogView.Read(snapshot.Reader, snapshot.IngameStateAddress);
    }

    private static string OneLine(string text)
        => string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string Format(ElementGeometry.Rect? rect)
        => rect is { } r ? $"{r.X:F0},{r.Y:F0},{r.Width:F0},{r.Height:F0}" : "none";

    private sealed record Npc(string Name, string Path);
}
