using BubblesBot.Bot.Input;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Guarded transition from Kirac's exact The Formed option to its invitation surface.</summary>
public sealed class GuardianKiracInvitationOpenLiveTest : ILiveTestCase
{
    private const string OptionText = "Invitation: The Formed";
    private static readonly IReadOnlySet<string> AllowedPanels =
        new HashSet<string>(StringComparer.Ordinal) { "NpcDialog" };

    public string Id => "G-02-kirac-formed-open";
    public string Name => "Open Kirac The Formed invitation";
    public string Description => "Selects Kirac's exact Invitation: The Formed control and fingerprints the resulting invitation/device surface.";
    public string ManualSetup => "In the hideout with Commander Kirac's dialog open and Invitation: The Formed visible. Hold no item and keep PoE focused.";
    public LiveTestMutation Mutation => LiveTestMutation.Reversible;
    public bool DrivesInput => true;
    public IReadOnlySet<string> AllowedBlockingPanels => AllowedPanels;

    public async Task<LiveTestCaseResult> RunAsync(
        LiveTestContext context,
        CancellationToken cancellationToken)
    {
        var dialog = ReadDialog(context);
        context.Check(dialog.IsOpen, "Kirac dialog", $"panel=0x{(long)dialog.Panel:X}");
        var matches = dialog.FindExact(OptionText)
            .Where(x => x.Rect is { Width: > 0, Height: > 0 })
            .ToArray();
        context.Check(matches.Length == 1, "The Formed option identity", $"matches={matches.Length}");
        if (!dialog.IsOpen || matches.Length != 1 || matches[0].Rect is not { } rect)
            return LiveTestCaseResult.Blocked(
                "Kirac dialog does not expose one exact The Formed option", "PreparedStateMismatch");

        var window = context.Snapshot().Window;
        context.Check(rect.IntersectsWindow(window.Width, window.Height), "The Formed option geometry", rect.ToString());
        if (!rect.IntersectsWindow(window.Width, window.Height))
            return LiveTestCaseResult.Blocked("The Formed option is off-screen", "InvalidGeometry");

        var stable = ReadDialog(context).FindExact(OptionText)
            .SingleOrDefault(x => x.Element == matches[0].Element);
        context.Check(stable?.Rect is not null, "The Formed option stable",
            $"element=0x{(long)matches[0].Element:X} path={matches[0].TreePath}");
        if (stable?.Rect is not { } stableRect)
            return LiveTestCaseResult.Fail("The Formed option changed before click", "TargetChanged");

        var screen = window.ToScreen(stableRect.CenterX, stableRect.CenterY);
        var click = await context.VerifiedClickAsync(
            screen.X, screen.Y, ClickIntent.InteractUi, "select Kirac Invitation: The Formed",
            () => !ReadDialog(context).FindExact(OptionText).Any(),
            timeoutMs: 3_000, cancellationToken);
        if (click != ActionOutcome.Confirmed)
            return LiveTestCaseResult.Fail("The Formed selection produced no dialog transition", "DialogTransitionFailed");
        if (!await context.WaitForInputIdleAsync("after The Formed selection", 2_000, cancellationToken))
            return LiveTestCaseResult.Fail("input did not settle", "InputSettleFailed");

        var after = context.Snapshot();
        var visible = VisibleUiTextView.ReadInGame(after.Reader, after.IngameStateAddress, 20_000, 32);
        var relevant = visible.Elements
            .Where(x => x.Text.Contains("Formed", StringComparison.OrdinalIgnoreCase)
                || x.Text.Contains("Quantity", StringComparison.OrdinalIgnoreCase)
                || x.Text.Contains("Invitation", StringComparison.OrdinalIgnoreCase)
                || x.Text.Equals("Activate", StringComparison.OrdinalIgnoreCase))
            .Select(x => $"'{OneLine(x.Text)}'@{x.TreePath}")
            .ToArray();
        context.Observe("formed invitation surface", relevant.Length == 0
            ? $"atlasVisible={after.AtlasPanel.IsVisible} deviceVisible={after.AtlasPanel.IsDevicePanelVisible()}"
            : string.Join(" | ", relevant));

        var destinationProven = after.AtlasPanel.IsVisible
            || after.MapReceptacle.IsVisible
            || relevant.Any(x => x.Contains("Formed", StringComparison.OrdinalIgnoreCase)
                || x.Contains("Invitation", StringComparison.OrdinalIgnoreCase));
        context.Check(destinationProven, "invitation surface transition",
            $"atlasVisible={after.AtlasPanel.IsVisible} relevant={relevant.Length}");
        return destinationProven
            ? LiveTestCaseResult.Pass("selected exact Kirac The Formed option and captured its invitation surface", "InvitationSurfaceOpened")
            : LiveTestCaseResult.Fail("dialog closed without a recognizable invitation surface", "InvitationSurfaceUnreadable");
    }

    private static NpcDialogView ReadDialog(LiveTestContext context)
    {
        var snapshot = context.Snapshot();
        return NpcDialogView.Read(snapshot.Reader, snapshot.IngameStateAddress);
    }

    private static string OneLine(string text)
        => text.Replace('\r', ' ').Replace('\n', '|').Trim();
}
