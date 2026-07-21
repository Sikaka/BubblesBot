using BubblesBot.Bot.Input;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>
/// First production-input validation: toggle inventory to the opposite state and restore the
/// exact baseline through verified key taps. Safe only in a manually confirmed town/hideout.
/// </summary>
public sealed class InventoryRoundTripLiveTest : ILiveTestCase
{
    private const int InventoryVk = 0x49; // I

    public string Id => "H-01-input-roundtrip";
    public string Name => "Production input/read round trip";
    public string Description => "Uses the real InputRouter to toggle Inventory, verify the read, then restore baseline.";
    public string ManualSetup => "Stand alive in a safe town/hideout with no NPC/modal dialog open. Leave PoE focused.";
    public LiveTestMutation Mutation => LiveTestMutation.Reversible;
    public bool DrivesInput => true;

    public async Task<LiveTestCaseResult> RunAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        var baselineSnapshot = context.Snapshot();
        var baselineOpen = baselineSnapshot.OpenPanels.IsOpen("InventoryPanel");
        context.Observe("inventory baseline", baselineOpen ? "open" : "closed",
            new Dictionary<string, object?>
            {
                ["openPanels"] = baselineSnapshot.OpenPanels.Open.ToArray(),
                ["inventoryOpen"] = baselineOpen,
            });

        var reachedOpposite = false;
        var restored = false;
        try
        {
            var first = await context.VerifiedTapKeyAsync(
                InventoryVk,
                ClickIntent.InteractUi,
                baselineOpen ? "close inventory" : "open inventory",
                () => context.Snapshot().OpenPanels.IsOpen("InventoryPanel") != baselineOpen,
                timeoutMs: 2_000,
                cancellationToken);
            reachedOpposite = first == ActionOutcome.Confirmed;

            var afterFirst = context.Snapshot();
            var oppositeOpen = afterFirst.OpenPanels.IsOpen("InventoryPanel");
            context.Check(oppositeOpen != baselineOpen, "inventory opposite state",
                $"baseline={baselineOpen} observed={oppositeOpen}");
            var blocking = afterFirst.OpenPanels.BlockingOpen();
            context.Check(blocking.Count == 0, "no unexpected blocking panel",
                blocking.Count == 0 ? "none" : string.Join(", ", blocking));
            if (oppositeOpen)
            {
                var inventory = afterFirst.Inventory;
                context.Observe("visible inventory contents", string.Join(" | ", inventory.Items.Select(x =>
                    $"{x.Path} stack={x.StackSize} size={x.Width}x{x.Height} rect={x.Rect}")));
            }

            if (!reachedOpposite)
                return LiveTestCaseResult.Fail("inventory did not reach the opposite state", "ClickHadNoEffect");
            if (!await context.WaitForInputIdleAsync("after first inventory toggle", 1_500, cancellationToken))
                return LiveTestCaseResult.Fail("input router did not settle before inventory restoration", "InputSettleFailed");

            var second = await context.VerifiedTapKeyAsync(
                InventoryVk,
                ClickIntent.InteractUi,
                baselineOpen ? "restore inventory open" : "restore inventory closed",
                () => context.Snapshot().OpenPanels.IsOpen("InventoryPanel") == baselineOpen,
                timeoutMs: 2_000,
                cancellationToken);
            restored = second == ActionOutcome.Confirmed;

            var finalSnapshot = context.Snapshot();
            var finalOpen = finalSnapshot.OpenPanels.IsOpen("InventoryPanel");
            context.Check(finalOpen == baselineOpen, "inventory restored",
                $"baseline={baselineOpen} final={finalOpen}");
            var finalBlocking = finalSnapshot.OpenPanels.BlockingOpen();
            context.Check(finalBlocking.Count == 0, "final blocking panels",
                finalBlocking.Count == 0 ? "none" : string.Join(", ", finalBlocking));

            return restored
                ? LiveTestCaseResult.Pass("inventory state toggled and exactly restored", "CompletedAndRestored")
                : LiveTestCaseResult.Fail("inventory reached the opposite state but did not restore", "RestoreFailed");
        }
        finally
        {
            // Best-effort state restore for failures between the two verified actions. This is
            // still gated by foreground/character/area checks; it never guesses after setup drift.
            if (!restored && context.GameState == GameStateKind.InGame)
            {
                var current = context.Snapshot().OpenPanels.IsOpen("InventoryPanel");
                if (current != baselineOpen)
                {
                    context.Observe("failure recovery", "attempting one verified inventory restore");
                    _ = await context.VerifiedTapKeyAsync(
                        InventoryVk,
                        ClickIntent.InteractUi,
                        "failure recovery: restore inventory baseline",
                        () => context.Snapshot().OpenPanels.IsOpen("InventoryPanel") == baselineOpen,
                        timeoutMs: 2_000,
                        cancellationToken);
                }
            }
            context.CancelAllInput();
        }
    }
}
