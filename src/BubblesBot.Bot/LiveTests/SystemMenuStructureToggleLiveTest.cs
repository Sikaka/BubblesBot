using BubblesBot.Bot.Input;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Prove the system menu round trip through guarded visual-oracle clicks.</summary>
public sealed class SystemMenuStructureToggleLiveTest : ILiveTestCase
{
    private const int EscapeScanCode = 0x01;

    public string Id => "U-10-system-menu-structure-toggle";
    public string Name => "System menu visual round trip";
    public string Description => "Starting from a clean HUD at 1920x1080, opens and closes the system menu through guarded Escape scan-code taps and validates both outcomes from repeated button-band captures.";
    public string ManualSetup => "In a safe town/hideout, close ordinary UI and the system menu, hold no item, dismiss unrelated desktop dialogs, and leave PoE foreground at 1920x1080.";
    public LiveTestMutation Mutation => LiveTestMutation.Reversible;
    public bool DrivesInput => true;

    public async Task<LiveTestCaseResult> RunAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        var window = context.Snapshot().Window;
        context.Check(window.Width == SystemMenuVisualOracle.SupportedWidth
            && window.Height == SystemMenuVisualOracle.SupportedHeight,
            "supported visual-oracle geometry", $"window={window.Width}x{window.Height} at ({window.OriginX},{window.OriginY})");
        if (window.Width != SystemMenuVisualOracle.SupportedWidth
            || window.Height != SystemMenuVisualOracle.SupportedHeight)
            return LiveTestCaseResult.Blocked("the captured system-menu visual oracle supports only 1920x1080", "UnsupportedWindowGeometry");

        var baseline = SystemMenuVisualOracle.Read(window);
        context.Check(baseline.CaptureSucceeded, "clean-HUD frame capture", baseline.Detail);
        context.Check(!baseline.IsOpen, "system menu closed baseline", baseline.Detail);
        if (!baseline.CaptureSucceeded || baseline.IsOpen)
            return LiveTestCaseResult.Blocked("the prepared frame is not a classified clean HUD", "CleanHudSetupMissing");

        var closedStructure = ReadStructure(context);
        var open = await context.VerifiedTapScanCodeAsync(
            EscapeScanCode,
            ClickIntent.InteractUi,
            "open system menu through Escape hardware scan code 0x01",
            () => SystemMenuVisualOracle.Read(context.Snapshot().Window).IsOpen,
            timeoutMs: 2_000,
            cancellationToken);
        if (open != ActionOutcome.Confirmed)
            return LiveTestCaseResult.Fail("Escape scan-code tap did not produce the classified six-button frame", "MenuOpenFailed");
        if (!await context.WaitForInputIdleAsync("after system menu open", 1_500, cancellationToken))
            return LiveTestCaseResult.Fail("input did not settle after menu open", "InputSettleFailed");

        var opened = SystemMenuVisualOracle.Read(context.Snapshot().Window);
        context.Check(opened.CaptureSucceeded && opened.IsOpen, "system menu visual classification", opened.Detail);
        var openStructure = ReadStructure(context);
        context.Observe("system menu memory comparison",
            $"closedVisible={closedStructure.Nodes.Count} openVisible={openStructure.Nodes.Count}; generic visibility is diagnostic only",
            new Dictionary<string, object?>
            {
                ["closedVisibleNodes"] = closedStructure.Nodes.Count,
                ["openVisibleNodes"] = openStructure.Nodes.Count,
                ["visual"] = opened.Detail,
            });

        var close = await context.VerifiedTapScanCodeAsync(
            EscapeScanCode,
            ClickIntent.InteractUi,
            "close classified system menu through Escape hardware scan code 0x01",
            () =>
            {
                var visual = SystemMenuVisualOracle.Read(context.Snapshot().Window);
                return visual.CaptureSucceeded && !visual.IsOpen;
            },
            timeoutMs: 2_000,
            cancellationToken);
        if (close != ActionOutcome.Confirmed)
            return LiveTestCaseResult.Fail("Escape scan-code tap did not remove the classified menu frame", "MenuCloseFailed");
        if (!await context.WaitForInputIdleAsync("after system menu close", 1_500, cancellationToken))
            return LiveTestCaseResult.Fail("input did not settle after menu close", "InputSettleFailed");

        var restored = SystemMenuVisualOracle.Read(context.Snapshot().Window);
        context.Check(restored.CaptureSucceeded && !restored.IsOpen, "clean-HUD visual restored", restored.Detail);
        if (!restored.CaptureSucceeded || restored.IsOpen)
            return LiveTestCaseResult.Fail("clean HUD visual did not restore after Resume Game", "RestoreMismatch");

        // Repeatable runs share the production aggregate edge-action budget. Leave enough room
        // between two-action iterations that a fourth iteration cannot collide with the
        // six-actions-per-second server-kick guard.
        await Task.Delay(400, cancellationToken);

        return LiveTestCaseResult.Pass(
            $"guarded Escape scan-code taps opened and closed the visually classified six-button system menu ({opened.Detail})",
            "CompletedAndRestored");
    }

    private static VisibleUiStructureView ReadStructure(LiveTestContext context)
    {
        var snapshot = context.Snapshot();
        return VisibleUiStructureView.ReadInGame(snapshot.Reader, snapshot.IngameStateAddress);
    }
}
