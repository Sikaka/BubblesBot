using BubblesBot.Bot.Input;
using BubblesBot.Core.Game;

namespace BubblesBot.Bot.LiveTests;

/// <summary>
/// Reversible passive-tree diagnostic (checklist S-05 / U-08 groundwork). Reads the ServerData
/// passive-point counts, opens the passive tree through the exact keybind, re-reads the counts, and
/// closes it — restoring the panel-free baseline. The point of the test is the <b>before/after</b>
/// delta: S-05 R-phase found those ServerData counts reading zero on a level-8 character, and the
/// leading hypothesis is that they are lazy until the passive panel forces a server sync. This test
/// proves or refutes that without allocating anything.
/// </summary>
public sealed class PassiveTreeInspectLiveTest : ILiveTestCase
{
    private const int PassiveTreeVk = 0x50; // 'P'
    private const int EscapeVk = 0x1B;

    public string Id => "S-05-passive-tree-inspect";
    public string Name => "Passive tree open/read/close diagnostic";
    public string Description => "Reads ServerData passive-point counts, opens the passive tree via 'P', re-reads the counts to test whether they populate on open, observes the TreePanel, and closes it back to the panel-free baseline.";
    public string ManualSetup => "Stand alive in a safe town/hideout with all UI windows closed. Hold no item and leave PoE focused.";
    public LiveTestMutation Mutation => LiveTestMutation.Reversible;
    public bool DrivesInput => true;

    public async Task<LiveTestCaseResult> RunAsync(
        LiveTestContext context,
        CancellationToken cancellationToken)
    {
        var start = context.Snapshot();
        var blocking = start.OpenPanels.BlockingOpen();
        context.Check(blocking.Count == 0, "no blocking panel at start", blocking.Count == 0 ? "none" : string.Join(", ", blocking));
        context.Check(!start.OpenPanels.IsOpen("TreePanel"), "passive tree closed at start", $"open=[{string.Join(", ", start.OpenPanels.Open)}]");
        if (blocking.Count > 0 || start.OpenPanels.IsOpen("TreePanel"))
            return LiveTestCaseResult.Blocked("did not start from a clean, tree-closed baseline", "CleanUiBaselineMissing");

        var before = ServerPlayerInfoReader.Read(start.Reader, start.IngameDataAddress);
        context.Check(before.IsAvailable, "ServerPlayerData readable before open", $"available={before.IsAvailable}");
        context.Observe("passive counts BEFORE open", Describe(before), Snapshot(before));

        // Open the passive tree.
        var open = await context.VerifiedTapKeyAsync(
            PassiveTreeVk,
            ClickIntent.InteractUi,
            "open passive tree via 'P'",
            () => context.Snapshot().OpenPanels.IsOpen("TreePanel"),
            timeoutMs: 3_000,
            cancellationToken);
        if (open != ActionOutcome.Confirmed)
            return LiveTestCaseResult.Fail("passive tree did not open on 'P'", "PassiveTreeOpenFailed");
        if (!await context.WaitForInputIdleAsync("after passive tree open", 1_500, cancellationToken))
            return LiveTestCaseResult.Fail("input did not settle after opening passive tree", "InputSettleFailed");

        // Give any server sync a chance to land while the panel is open, polling for a populated read.
        // The populate OUTCOME is diagnostic data, not a pass/fail assertion — the capability under
        // test is open/read/close, so this is a non-failing poll that feeds the observation below.
        var populated = false;
        for (var i = 0; i < 40 && !populated; i++)
        {
            var live = ServerPlayerInfoReader.Read(context.Snapshot().Reader, context.IngameDataAddress);
            populated = live.AvailablePassivePoints > 0 || live.AllocatedPassiveSpanBytes > 0 || live.PassiveRefundPointsLeft > 0;
            if (!populated) await Task.Delay(75, cancellationToken);
        }

        var opened = context.Snapshot();
        var after = ServerPlayerInfoReader.Read(opened.Reader, opened.IngameDataAddress);
        context.Observe("passive counts AFTER open", Describe(after), Snapshot(after));
        var treeState = opened.OpenPanels.States.FirstOrDefault(x => x.Name == "TreePanel");
        context.Observe("TreePanel element state (U-08 groundwork)",
            $"present={treeState.Present} visible={treeState.Visible}");
        context.Observe("populate diagnostic",
            populated
                ? "ServerData passive counts POPULATED after opening the tree — counts are lazy, not genuinely zero."
                : "ServerData passive counts STILL zero/empty after opening the tree — either genuinely zero on this character, or the UI panel (not ServerData) is the only source.");

        // Close the passive tree (P toggles; Escape as fallback path not needed — P is the toggle).
        var close = await context.VerifiedTapKeyAsync(
            PassiveTreeVk,
            ClickIntent.InteractUi,
            "close passive tree via 'P'",
            () => !context.Snapshot().OpenPanels.IsOpen("TreePanel"),
            timeoutMs: 3_000,
            cancellationToken);
        if (close != ActionOutcome.Confirmed)
        {
            // Fallback: Escape closes any open panel.
            close = await context.VerifiedTapKeyAsync(
                EscapeVk,
                ClickIntent.InteractUi,
                "close passive tree via Escape (fallback)",
                () => !context.Snapshot().OpenPanels.IsOpen("TreePanel"),
                timeoutMs: 3_000,
                cancellationToken);
        }
        if (close != ActionOutcome.Confirmed)
            return LiveTestCaseResult.Fail("passive tree did not close", "PassiveTreeCloseFailed");
        if (!await context.WaitForInputIdleAsync("after passive tree close", 1_500, cancellationToken))
            return LiveTestCaseResult.Fail("input did not settle after closing passive tree", "InputSettleFailed");

        var restored = context.Snapshot();
        var restoredBlocking = restored.OpenPanels.BlockingOpen();
        context.Check(!restored.OpenPanels.IsOpen("TreePanel") && restoredBlocking.Count == 0,
            "panel-free baseline restored", $"tree={restored.OpenPanels.IsOpen("TreePanel")} blocking=[{string.Join(", ", restoredBlocking)}]");
        if (restored.OpenPanels.IsOpen("TreePanel") || restoredBlocking.Count > 0)
            return LiveTestCaseResult.Fail("baseline not restored after passive tree diagnostic", "RestoreFailed");

        return LiveTestCaseResult.Pass(
            $"passive tree opened, read, and closed; counts {(populated ? "populated on open (lazy)" : "remained zero")}; baseline restored",
            "CompletedAndRestored");
    }

    private static string Describe(ServerPlayerInfoReader.Info i)
        => $"class={i.PlayerClass} level={i.Level} free={i.FreePassiveSkillPointsLeft} quest={i.QuestPassiveSkillPoints} "
         + $"refund={i.PassiveRefundPointsLeft} available={i.AvailablePassivePoints} allocatedBytes={i.AllocatedPassiveSpanBytes} "
         + $"ascTotal={i.TotalAscendancyPoints} ascSpent={i.SpentAscendancyPoints}";

    private static IReadOnlyDictionary<string, object?> Snapshot(ServerPlayerInfoReader.Info i)
        => new Dictionary<string, object?>
        {
            ["class"] = i.PlayerClass,
            ["level"] = i.Level,
            ["free"] = i.FreePassiveSkillPointsLeft,
            ["quest"] = i.QuestPassiveSkillPoints,
            ["refund"] = i.PassiveRefundPointsLeft,
            ["available"] = i.AvailablePassivePoints,
            ["allocatedBytes"] = i.AllocatedPassiveSpanBytes,
            ["ascTotal"] = i.TotalAscendancyPoints,
            ["ascSpent"] = i.SpentAscendancyPoints,
        };
}
