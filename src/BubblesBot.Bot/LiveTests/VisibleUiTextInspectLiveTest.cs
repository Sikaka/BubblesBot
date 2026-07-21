using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Read-only visible UI text dump used to discover an unpromoted menu structure.</summary>
public sealed class VisibleUiTextInspectLiveTest : ILiveTestCase
{
    public string Id => "U-10-visible-ui-text-inspect";
    public string Name => "Visible in-game UI text inspection";
    public string Description => "Dumps bounded deeply-visible UIRoot text, geometry, and tree paths without input.";
    public string ManualSetup => "Any loaded in-game state, ideally with the UI surface under research currently open.";
    public LiveTestMutation Mutation => LiveTestMutation.ReadOnly;
    public bool DrivesInput => false;
    public IReadOnlySet<string> AllowedBlockingPanels => OpenPanelsView.BlockingPanels;

    public Task<LiveTestCaseResult> RunAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        var snapshot = context.Snapshot();
        var view = VisibleUiTextView.ReadInGame(snapshot.Reader, snapshot.IngameStateAddress, 20_000, 32);
        context.Check(view.Root != 0, "UIRoot", $"0x{(long)view.Root:X}");
        context.Check(view.Visited > 0, "visible UI traversal", $"visited={view.Visited} text={view.Elements.Count}");
        foreach (var item in view.Elements)
            context.Observe("visible UI text",
                $"'{OneLine(item.Text)}' element=0x{(long)item.Element:X} path={item.TreePath} rect={item.Rect} children={item.ChildCount}");
        return Task.FromResult(LiveTestCaseResult.Pass(
            $"captured {view.Elements.Count} visible text elements from {view.Visited} visited elements",
            "ReadOnlyCapture"));
    }

    private static string OneLine(string text)
        => text.Replace('\r', ' ').Replace('\n', '|').Trim();
}
