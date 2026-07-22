using BubblesBot.Core;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;
using BubblesBot.Bot.Strategies;

namespace BubblesBot.Bot.LiveTests;

/// <summary>
/// Read-only fingerprint of the element currently under the cursor. This is useful for UI such
/// as atlas nodes whose rendered tooltip is not attached beneath the ordinary visible UIRoot.
/// </summary>
public sealed class UiHoverInspectLiveTest : ILiveTestCase
{
    public string Id => "U-11-current-hover-inspect";
    public string Name => "Current UI hover inspection";
    public string Description => "Dumps the current UIHover ancestry and every readable tooltip attached to it without moving the cursor.";
    public string ManualSetup => "Hover the UI control under research and leave the cursor still.";
    public LiveTestMutation Mutation => LiveTestMutation.ReadOnly;
    public bool DrivesInput => false;
    public IReadOnlySet<string> AllowedBlockingPanels => OpenPanelsView.BlockingPanels;

    public Task<LiveTestCaseResult> RunAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        var snapshot = context.Snapshot();
        var productionHover = UiHoverView.Read(snapshot.Reader, snapshot.IngameStateAddress);
        var hover = productionHover.Element;
        if (hover == 0)
            return Task.FromResult(LiveTestCaseResult.Blocked(
                "UIHover is empty; keep the cursor over the target control", "HoverMissing"));

        context.Check(true, "UIHover", $"0x{(long)hover:X}");
        context.Observe("production hover tooltip",
            $"root=0x{(long)productionHover.TooltipRoot:X} lines=[{string.Join(" || ", productionHover.TooltipLines)}]");
        var guardian = GuardianRotationPolicy.ClassifyTooltip(productionHover.TooltipLines);
        if (guardian.WitnessStatus != GuardianWitnessStatus.Unknown)
        {
            var atlasChild = snapshot.AtlasPanel.AtlasCanvasDirectChildForHover(hover);
            context.Observe("guardian witness state",
                $"map='{guardian.MapName}' status={guardian.WitnessStatus} " +
                $"uiIndex={atlasChild} dataIndex={atlasChild - Systems.MapDeviceSystem.CurrentAtlasNodeUiPrefix}");
        }
        var current = hover;
        var tooltipTexts = new List<string>();
        for (var depth = 0; depth < 32 && current != 0; depth++)
        {
            var rect = ElementGeometry.TryReadRect(snapshot.Reader, current);
            var ownText = ReadOwnText(snapshot.Reader, current);
            snapshot.Reader.TryReadStruct<nint>(current + KnownOffsets.Element.Tooltip, out var tooltip);
            snapshot.Reader.TryReadStruct<nint>(current + KnownOffsets.Element.RenderedTooltip, out var renderedTooltip);
            context.Observe("hover ancestry",
                $"depth={depth} element=0x{(long)current:X} rect={rect} text='{OneLine(ownText)}' " +
                $"tooltip=0x{(long)tooltip:X} renderedTooltip=0x{(long)renderedTooltip:X}");

            foreach (var (kind, root) in new[] { ("tooltip", tooltip), ("rendered-tooltip", renderedTooltip) })
            {
                if (root == 0) continue;
                var text = ReadTooltip(snapshot.Reader, root);
                context.Observe(kind, $"ownerDepth={depth} root=0x{(long)root:X} text=[{text}]");
                if (text.Length > 0) tooltipTexts.Add(text);
            }
            if (depth == 0 && tooltip != 0)
                InspectTooltipWrapper(snapshot.Reader, tooltip, context, tooltipTexts);

            if (!snapshot.Reader.TryReadStruct<nint>(current + KnownOffsets.Element.Parent, out var parent)
                || parent == current)
                break;
            current = parent;
        }

        var distinct = tooltipTexts.Distinct(StringComparer.Ordinal).ToArray();
        context.Check(distinct.Length > 0, "readable hover tooltip", $"count={distinct.Length}");
        return Task.FromResult(distinct.Length > 0
            ? LiveTestCaseResult.Pass(
                $"captured {distinct.Length} tooltip fingerprint(s) from UIHover 0x{(long)hover:X}",
                "ReadOnlyCapture")
            : LiveTestCaseResult.Fail(
                "the current hover ancestry exposed no readable tooltip", "TooltipUnreadable"));
    }

    private static string ReadOwnText(MemoryReader reader, nint element)
    {
        var text = NativeString.Read(reader, element + KnownOffsets.Element.TextNoTags);
        return string.IsNullOrWhiteSpace(text)
            ? NativeString.Read(reader, element + KnownOffsets.Element.Text)
            : text;
    }

    private static string ReadTooltip(MemoryReader reader, nint root)
    {
        var lines = new List<string>();
        var queue = new Queue<(nint Address, int Depth)>();
        var seen = new HashSet<nint>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0 && seen.Count < 1024)
        {
            var (address, depth) = queue.Dequeue();
            if (!seen.Add(address)) continue;
            var element = ElementReader.TryReadSnapshot(reader, address, 256);
            if (element is null) continue;
            var text = ReadOwnText(reader, address);
            lines.AddRange(text.Split(['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (depth >= 16) continue;
            foreach (var child in element.Children) queue.Enqueue((child, depth + 1));
        }
        return string.Join(" || ", lines.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
    }

    private static void InspectTooltipWrapper(
        MemoryReader reader,
        nint wrapper,
        LiveTestContext context,
        List<string> tooltipTexts)
    {
        var reported = 0;
        for (var offset = 0; offset < 0x800 && reported < 128; offset += sizeof(long))
        {
            if (!reader.TryReadStruct<nint>(wrapper + offset, out var candidate)
                || (long)candidate < 0x10000)
                continue;
            var element = ElementReader.TryReadSnapshot(reader, candidate, 256);
            if (element is null) continue;
            var text = ReadTooltip(reader, candidate);
            if (!text.Any(char.IsLetterOrDigit)) continue;
            context.Observe("tooltip wrapper pointer",
                $"wrapper=0x{(long)wrapper:X} +0x{offset:X} -> 0x{(long)candidate:X} " +
                $"children={element.Children.Count} text=[{text}]");
            tooltipTexts.Add(text);
            reported++;
        }
    }

    private static string OneLine(string text)
        => text.Replace('\r', ' ').Replace('\n', '|').Trim();
}
