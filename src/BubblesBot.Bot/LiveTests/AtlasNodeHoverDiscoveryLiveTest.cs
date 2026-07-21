using BubblesBot.Core;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;
using BubblesBot.Bot.Strategies;
using BubblesBot.Bot.Systems;

namespace BubblesBot.Bot.LiveTests;

/// <summary>
/// Guarded atlas-node selection contract. It targets one catalogued node only and refuses
/// the click unless UIHover proves the exact atlas child is topmost at the click point.
/// </summary>
public sealed class AtlasNodeHoverDiscoveryLiveTest : ILiveTestCase
{
    private const string TargetMapName = "Strand";

    public string Id => "A-10-atlas-node-hover";
    public string Name => "Atlas node hover discovery";
    public string Description => $"Hovers/selects atlas canvas children and reports the UI child/data index for {TargetMapName}.";
    public string ManualSetup => "Open the Atlas/map-device panel with no map staged and keep PoE focused.";
    public LiveTestMutation Mutation => LiveTestMutation.Reversible;
    public bool DrivesInput => true;

    public async Task<LiveTestCaseResult> RunAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        context.SetAllowedBlockingPanels(new HashSet<string>(StringComparer.Ordinal) { "AtlasPanel" });
        var initial = context.Snapshot();
        var atlas = initial.AtlasPanel;
        if (!atlas.IsVisible)
            return LiveTestCaseResult.Blocked("Atlas panel is not visible", "PanelMissing");
        var count = atlas.AtlasCanvasChildCount();
        context.Check(count is > 4 and < 1024, "atlas canvas child count", $"count={count}");
        if (count is <= 4 or >= 1024)
            return LiveTestCaseResult.Blocked("atlas canvas child array is unreadable", "CanvasUnreadable");

        if (!AtlasNodeCatalog.TryGetDataIndex(TargetMapName, out var dataIndex))
            return LiveTestCaseResult.Blocked($"{TargetMapName} is absent from AtlasNodeCatalog", "CatalogMissing");

        var uiIndex = dataIndex + MapDeviceSystem.CurrentAtlasNodeUiPrefix;
        var rect = initial.AtlasPanel.AtlasCanvasChildRect(uiIndex);
        if (rect is not { Width: > 2, Height: > 2 })
            return LiveTestCaseResult.Blocked($"atlas child {uiIndex} has no usable rectangle", "NodeGeometryMissing");
        var cx = (int)rect.Value.CenterX;
        var cy = (int)rect.Value.CenterY;
        var window = initial.Window;
        if (cx < 0 || cy < 0 || cx >= window.Width || cy >= window.Height)
            return LiveTestCaseResult.Blocked(
                $"{TargetMapName} child {uiIndex} is off screen at ({cx},{cy}); pan required",
                "NodeOffscreen");

        var point = window.ToScreen(cx, cy);
        await context.HoverAsync(point.X, point.Y, 250, cancellationToken);
        var hovered = context.Snapshot();
        hovered.Reader.TryReadStruct<nint>(
            hovered.IngameStateAddress + KnownOffsets.IngameState.UIHover, out var hover);
        var ownsHover = hovered.AtlasPanel.HoverBelongsToAtlasCanvasChild(hover, uiIndex);
        context.Check(ownsHover, "exact atlas-node hover identity",
            $"target={TargetMapName} dataIndex={dataIndex} uiIndex={uiIndex} point=({cx},{cy}) hover=0x{(long)hover:X}");
        if (!ownsHover)
            return LiveTestCaseResult.Blocked(
                $"{TargetMapName} child {uiIndex} is obscured or not hoverable; no click sent",
                "NodeObscured");

        var outcome = await context.VerifiedClickAsync(
            point.X, point.Y, BubblesBot.Bot.Input.ClickIntent.InteractUi,
            $"select verified {TargetMapName} atlas child {uiIndex}",
            () => context.Snapshot().AtlasPanel.SelectedMapName()
                .Equals(TargetMapName, StringComparison.OrdinalIgnoreCase),
            2_500, cancellationToken);
        if (outcome != BubblesBot.Bot.Input.ActionOutcome.Confirmed)
            return LiveTestCaseResult.Fail(
                $"verified child {uiIndex} did not select {TargetMapName}",
                "SelectionNotConfirmed");

        context.Check(true, $"{TargetMapName} atlas node",
            $"uiIndex={uiIndex} dataIndex={dataIndex} selected='{context.Snapshot().AtlasPanel.SelectedMapName()}'");
        return LiveTestCaseResult.Pass(
            $"{TargetMapName} selected at atlas UI child {uiIndex}, data index {dataIndex}",
            "AtlasNodeLocated");
    }

    private static string ReadTooltipFromAncestry(MemoryReader reader, nint start)
    {
        var current = start;
        for (var depth = 0; depth < 20 && current != 0; depth++)
        {
            foreach (var offset in new[] { KnownOffsets.Element.RenderedTooltip, KnownOffsets.Element.Tooltip })
            {
                if (!reader.TryReadStruct<nint>(current + offset, out var tooltip) || tooltip == 0) continue;
                var text = ReadTooltip(reader, tooltip);
                if (text.Length > 0) return text;
            }
            if (!reader.TryReadStruct<nint>(current + KnownOffsets.Element.Parent, out var parent)
                || parent == current) break;
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
            var element = ElementReader.TryReadSnapshot(reader, address, 128);
            if (element is null) continue;
            if (ElementReader.IsVisibleDeep(reader, address))
            {
                var text = NativeString.Read(reader, address + KnownOffsets.Element.TextNoTags);
                if (string.IsNullOrWhiteSpace(text))
                    text = NativeString.Read(reader, address + KnownOffsets.Element.Text);
                lines.AddRange(text.Split(['\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
            if (depth >= 10) continue;
            foreach (var child in element.Children) queue.Enqueue((child, depth + 1));
        }
        return string.Join(" || ", lines.Where(line => !string.IsNullOrWhiteSpace(line)).Distinct());
    }
}
