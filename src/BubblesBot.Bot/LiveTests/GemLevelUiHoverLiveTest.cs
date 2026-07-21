using BubblesBot.Core;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Hover-only identity research for gem level-up controls.</summary>
public sealed class GemLevelUiHoverLiveTest : ILiveTestCase
{
    public string Id => "U-07-gem-level-ui-hover";
    public string Name => "Gem level-up control hover identity";
    public string Description => "Structurally resolves All, per-row level, and dismiss controls, then records exact UIHover and rendered tooltips without clicking.";
    public string ManualSetup => "Have Chance to Bleed and Splitting Steel ready to level with the All control visible. Keep PoE focused and do not click the controls.";
    public LiveTestMutation Mutation => LiveTestMutation.Reversible;
    public bool DrivesInput => true;

    public async Task<LiveTestCaseResult> RunAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        var snapshot = context.Snapshot();
        if (!TryGetPanel(snapshot, out var panel))
            return LiveTestCaseResult.Blocked("GemLvlUpPanel was unreadable", "PanelMissing");

        var nodes = ReadNodes(snapshot.Reader, panel);
        var rows = nodes.Where(x => x.Visible && x.Children.Count == 4)
            .Where(IsGemRowShape).OrderBy(x => x.Rect!.Value.Y).ToArray();
        var allButtons = nodes.Where(x => x.Visible && x.Children.Count == 1 && x.Rect is { } r
                && r.Width is > 60 and < 75 && r.Height is > 25 and < 35)
            .Where(x => DescendantText(snapshot.Reader, x.Address, "all")).ToArray();
        context.Check(rows.Length == 2, "two structural gem rows", $"count={rows.Length}");
        context.Check(allButtons.Length == 1, "one structural All button", $"count={allButtons.Length}");
        if (rows.Length != 2 || allButtons.Length != 1)
            return LiveTestCaseResult.Blocked("two-row/All structure did not match", "PanelShapeMismatch");

        var all = allButtons[0];
        var allHover = await HoverControlAsync(context, all.Address, all.Rect!.Value, "All level control", cancellationToken);
        context.Observe("All hover identity", allHover);

        var tooltips = new List<string>();
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            var level = NodeFor(snapshot.Reader, row.Children[1]);
            var dismiss = NodeFor(snapshot.Reader, row.Children[0]);
            if (level?.Rect is not { } levelRect || dismiss?.Rect is not { } dismissRect)
                return LiveTestCaseResult.Fail($"row {index} controls were unreadable", "RowControlUnreadable");

            var levelHover = await HoverControlAsync(context, level.Address, levelRect,
                $"row {index} level control", cancellationToken);
            var dismissHover = await HoverControlAsync(context, dismiss.Address, dismissRect,
                $"row {index} dismiss control", cancellationToken);
            context.Observe("gem row hover identity",
                $"row={index} rowElement=0x{(long)row.Address:X} level=[{levelHover}] dismiss=[{dismissHover}]");
            tooltips.Add(levelHover);
            tooltips.Add(dismissHover);
        }

        var chance = tooltips.Any(x => x.Contains("Chance to Bleed", StringComparison.OrdinalIgnoreCase));
        var splitting = tooltips.Any(x => x.Contains("Splitting Steel", StringComparison.OrdinalIgnoreCase));
        context.Check(!chance && !splitting, "controls expose no tooltip identity",
            $"chanceToBleed={chance} splittingSteel={splitting}; row identity must come from GemLevelUpView's direct entity binding");

        var window = context.Snapshot().Window;
        await context.HoverAsync(window.OriginX + window.Width / 2, window.OriginY + 80, 150, cancellationToken);
        return LiveTestCaseResult.Pass(
            $"hovered structural All and two gem rows; tooltip identity chanceToBleed={chance} splittingSteel={splitting}",
            "ReadOnlyStructureCapture");
    }

    private static async Task<string> HoverControlAsync(
        LiveTestContext context,
        nint control,
        ElementGeometry.Rect rect,
        string label,
        CancellationToken cancellationToken)
    {
        var point = context.Snapshot().Window.ToScreen(rect.CenterX, rect.CenterY);
        await context.HoverAsync(point.X, point.Y, 180, cancellationToken);
        var reached = await context.WaitUntilAsync(label + " UIHover",
            () => ResolvesHoverTo(context.Snapshot(), control), 1_500, cancellationToken, 20);
        var snapshot = context.Snapshot();
        snapshot.Reader.TryReadStruct<nint>(snapshot.IngameStateAddress + KnownOffsets.IngameState.UIHover, out var hover);
        var tooltip = ReadTooltipFromAncestry(snapshot.Reader, hover);
        context.Check(reached, label + " exact ancestry",
            $"control=0x{(long)control:X} hover=0x{(long)hover:X} screen=({point.X},{point.Y}) tooltip=[{tooltip}]");
        return $"control=0x{(long)control:X} hover=0x{(long)hover:X} tooltip=[{tooltip}]";
    }

    private static string ReadTooltipFromAncestry(MemoryReader reader, nint start)
    {
        var current = start;
        for (var depth = 0; depth < 16 && current != 0; depth++)
        {
            foreach (var offset in new[] { KnownOffsets.Element.RenderedTooltip, KnownOffsets.Element.Tooltip })
            {
                if (!reader.TryReadStruct<nint>(current + offset, out var tooltip) || tooltip == 0) continue;
                var text = ReadTooltip(reader, tooltip);
                if (text.Length > 0) return text;
            }
            if (!reader.TryReadStruct<nint>(current + KnownOffsets.Element.Parent, out var parent) || parent == current)
                break;
            current = parent;
        }
        return string.Empty;
    }

    private static string ReadTooltip(MemoryReader reader, nint root)
    {
        var lines = new List<string>();
        var queue = new Queue<(nint Address, int Depth)>();
        var seen = new HashSet<nint>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0 && seen.Count < 512)
        {
            var (address, depth) = queue.Dequeue();
            if (!seen.Add(address)) continue;
            var element = ElementReader.TryReadSnapshot(reader, address, 64);
            if (element is null) continue;
            if (ElementReader.IsVisibleDeep(reader, address))
            {
                var text = NativeString.Read(reader, address + KnownOffsets.Element.TextNoTags);
                if (string.IsNullOrWhiteSpace(text)) text = NativeString.Read(reader, address + KnownOffsets.Element.Text);
                foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (!string.IsNullOrWhiteSpace(line)) lines.Add(line);
            }
            if (depth >= 10) continue;
            foreach (var child in element.Children) queue.Enqueue((child, depth + 1));
        }
        return string.Join(" || ", lines);
    }

    private static bool IsGemRowShape(Node row)
    {
        if (row.Rect is not { Width: > 240 and < 265, Height: > 50 and < 65 }) return false;
        return true;
    }

    private static bool DescendantText(MemoryReader reader, nint root, string expected)
    {
        var element = ElementReader.TryReadSnapshot(reader, root, 16);
        if (element is null) return false;
        foreach (var child in element.Children)
        {
            var text = NativeString.Read(reader, child + KnownOffsets.Element.TextNoTags);
            if (text.Equals(expected, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool ResolvesHoverTo(GameSnapshot snapshot, nint target)
    {
        snapshot.Reader.TryReadStruct<nint>(snapshot.IngameStateAddress + KnownOffsets.IngameState.UIHover, out var current);
        for (var depth = 0; depth < 24 && current != 0; depth++)
        {
            if (current == target) return true;
            if (!snapshot.Reader.TryReadStruct<nint>(current + KnownOffsets.Element.Parent, out var parent) || parent == current)
                break;
            current = parent;
        }
        return false;
    }

    private static bool TryGetPanel(GameSnapshot snapshot, out nint panel)
    {
        panel = 0;
        return snapshot.Reader.TryReadStruct<nint>(snapshot.IngameStateAddress + KnownOffsets.IngameState.IngameUi, out var ui)
            && ui != 0
            && snapshot.Reader.TryReadStruct<nint>(ui + KnownOffsets.IngameUiElements.GemLvlUpPanel, out panel)
            && panel != 0;
    }

    private static IReadOnlyList<Node> ReadNodes(MemoryReader reader, nint panel)
    {
        var nodes = new List<Node>();
        var queue = new Queue<(nint Address, int Depth)>();
        var seen = new HashSet<nint>();
        queue.Enqueue((panel, 0));
        while (queue.Count > 0 && seen.Count < 128)
        {
            var (address, depth) = queue.Dequeue();
            if (!seen.Add(address)) continue;
            var element = ElementReader.TryReadSnapshot(reader, address, 32);
            if (element is null) continue;
            nodes.Add(new Node(address, ElementGeometry.TryReadRect(reader, address),
                ElementReader.IsVisibleDeep(reader, address), element.Children));
            if (depth >= 8) continue;
            foreach (var child in element.Children) queue.Enqueue((child, depth + 1));
        }
        return nodes;
    }

    private static Node? NodeFor(MemoryReader reader, nint address)
    {
        var element = ElementReader.TryReadSnapshot(reader, address, 16);
        return element is null ? null : new Node(address, ElementGeometry.TryReadRect(reader, address),
            ElementReader.IsVisibleDeep(reader, address), element.Children);
    }

    private sealed record Node(
        nint Address,
        ElementGeometry.Rect? Rect,
        bool Visible,
        IReadOnlyList<nint> Children);
}
