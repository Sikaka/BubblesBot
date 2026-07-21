using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Read-only structural discovery for visible centered UI branches.</summary>
public sealed class VisibleUiBranchInspectLiveTest : ILiveTestCase
{
    public string Id => "U-10-visible-ui-branch-inspect";
    public string Name => "Visible centered UI branch inspection";
    public string Description => "Enumerates visible UIRoot elements near the window center, including paths, rectangles, flags, and child counts.";
    public string ManualSetup => "Open the system menu manually and leave PoE foreground. This test sends no input.";
    public LiveTestMutation Mutation => LiveTestMutation.ReadOnly;
    public bool DrivesInput => false;

    public Task<LiveTestCaseResult> RunAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        var game = context.Snapshot();
        if (!game.Reader.TryReadStruct<nint>(game.IngameStateAddress + KnownOffsets.IngameState.UIRoot, out var root)
            || root == 0)
            return Task.FromResult(LiveTestCaseResult.Fail("UIRoot was not readable", "UiRootMissing"));

        var queue = new Queue<(nint Address, string Path, int Depth)>();
        var seen = new HashSet<nint>();
        var candidates = new List<(nint Address, string Path, int Depth, ElementGeometry.Rect Rect, int Children, uint Flags)>();
        queue.Enqueue((root, "root", 0));
        while (queue.Count > 0 && seen.Count < 20_000)
        {
            var (address, path, depth) = queue.Dequeue();
            if (!seen.Add(address) || !ElementReader.IsVisibleDeep(game.Reader, address)) continue;
            var element = ElementReader.TryReadSnapshot(game.Reader, address, 512);
            if (element is null) continue;
            var rect = ElementGeometry.TryReadRect(game.Reader, address);
            game.Reader.TryReadStruct<uint>(address + KnownOffsets.Element.Flags, out var flags);
            if (rect is { Width: > 10, Height: > 10 } r
                && r.IntersectsWindow(game.Window.Width, game.Window.Height)
                && Math.Abs(r.CenterX - game.Window.Width / 2f) < 450
                && Math.Abs(r.CenterY - game.Window.Height / 2f) < 400)
                candidates.Add((address, path, depth, r, element.Children.Count, flags));

            if (depth >= 32) continue;
            for (var i = 0; i < element.Children.Count; i++)
                queue.Enqueue((element.Children[i], $"{path}/{i}", depth + 1));
        }

        var structural = candidates
            .Where(x => x.Children > 0)
            .OrderBy(x => x.Rect.Width * x.Rect.Height)
            .ThenByDescending(x => x.Depth)
            .Take(300)
            .ToArray();
        context.Check(seen.Count > 0, "visible structural traversal", $"visited={seen.Count} centered={candidates.Count} structural={structural.Length}");
        foreach (var item in structural)
            context.Observe("centered visible branch",
                $"element=0x{(long)item.Address:X} path={item.Path} depth={item.Depth} rect={item.Rect} children={item.Children} flags=0x{item.Flags:X8}");

        return Task.FromResult(LiveTestCaseResult.Pass(
            $"captured {structural.Length} centered visible structural elements from {seen.Count} visited nodes",
            "ReadOnlyCapture"));
    }
}
