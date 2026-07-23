using BubblesBot.Bot.Behaviors;
using BubblesBot.Bot.Input;
using BubblesBot.Bot.Settings;
using BubblesBot.Bot.Systems;
using BubblesBot.Core;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>
/// Integration proof for the reworked <see cref="StashTabSwitcher"/>: drives the REAL switcher to
/// select a named tab, confirming by the visible-content pointer (no server display index). Steps
/// the active tab away first (arrow key) so the switch is observable, then asserts the switcher
/// reports Succeeded and that we ended on the target's own content. Target defaults to "_2"; pass
/// a different name via --expect-reward.
/// </summary>
public sealed class StashSwitchToTargetLiveTest : ILiveTestCase
{
    private const int VkRight = 0x27;

    public string Id => "H-10-stash-switch-to-target";
    public string Name => "Stash switch to named tab (real switcher)";
    public string Description => "Drives the production StashTabSwitcher to select a named tab and confirms via content pointer.";
    public string ManualSetup => "Open the stash. The test steps the active tab then selects the target (default '_2').";
    public LiveTestMutation Mutation => LiveTestMutation.Reversible;
    public bool DrivesInput => true;

    private static readonly IReadOnlySet<string> AllowedPanels =
        new HashSet<string>(StringComparer.Ordinal)
        { "StashElement", "InventoryPanel", "NpcDialog", "GemLvlUpPanel", "InvitesPanel", "LeftPanel", "RightPanel" };
    public IReadOnlySet<string> AllowedBlockingPanels => AllowedPanels;

    public async Task<LiveTestCaseResult> RunAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        if (!context.Snapshot().StashInventory.IsOpen)
            return LiveTestCaseResult.Blocked("stash is not open", "PreparedStateMismatch");

        var target = string.IsNullOrWhiteSpace(context.ExpectedReward) ? "_2" : context.ExpectedReward.Trim();
        context.Observe("target tab", target);
        var startOnScreen = context.Snapshot().StashTabStrip.FindExact(target)
            .Any(c => c.Rect.CenterX >= 0 && c.Rect.CenterX < context.Snapshot().Window.Width);
        context.Observe("target on-screen at start", startOnScreen.ToString());

        // Step the active tab away (arrow Right) so selecting the target is a real, observable switch.
        var beforeStep = VisiblePtr(context.Snapshot());
        await context.VerifiedModifierTapKeyAsync(
            VkRight, [], ClickIntent.InteractUi, "step active tab away",
            () => VisiblePtr(context.Snapshot()) != beforeStep, 1_200, cancellationToken);
        await Task.Delay(300, cancellationToken);
        var afterStep = VisiblePtr(context.Snapshot());
        context.Observe("stepped away", $"visible 0x{beforeStep:X} -> 0x{afterStep:X}");

        // Drive the production switcher exactly as a mode would: Tick it, pumping input each loop.
        var settings = new BotSettings();
        var switcher = new StashTabSwitcher(() => context.Snapshot());
        switcher.Start(target, requireGeneralPurpose: false);

        var result = StashTabSwitcher.Result.InProgress;
        var deadline = 25;
        for (var i = 0; i < deadline * 20 && result == StashTabSwitcher.Result.InProgress; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snap = context.Snapshot();
            var ctx = new BehaviorContext(snap, context.Input, settings, null);
            result = switcher.Tick(ctx);
            context.Input.Tick();
            await Task.Delay(50, cancellationToken);
        }

        var endPtr = VisiblePtr(context.Snapshot());
        context.Observe("switcher finished", $"result={result} status='{switcher.Status}' visible=0x{endPtr:X}");

        context.Check(result == StashTabSwitcher.Result.Succeeded, "switcher selected the target",
            $"result={result} status='{switcher.Status}'");
        context.Check(endPtr != afterStep, "landed on a different tab than the stepped-away one",
            $"stepped=0x{afterStep:X} end=0x{endPtr:X}");

        return result == StashTabSwitcher.Result.Succeeded
            ? LiveTestCaseResult.Pass($"switcher selected '{target}' via click + content-pointer confirmation (status: {switcher.Status})", "TabSelected")
            : LiveTestCaseResult.Fail($"switcher did not select '{target}': {switcher.Status}", "TabNotSelected");
    }

    private static nint VisiblePtr(GameSnapshot s) => s.StashInventory.VisibleStashAddress;
}
