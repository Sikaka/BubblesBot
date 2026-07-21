using BubblesBot.Core;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Read/hover discovery for the specialized map-stash tier grid. Sends no clicks.</summary>
public sealed class MapStashTierHoverLiveTest : ILiveTestCase
{
    public string Id => "H-07-map-stash-tiers";
    public string Name => "Map stash tier hover catalog";
    public string Description => "Hovers every visible specialized map-stash tier control and records its tooltip identity without clicking.";
    public string ManualSetup => "Open the active Maps stash tab and inventory, with the cursor clear of items.";
    public LiveTestMutation Mutation => LiveTestMutation.Reversible;
    public bool DrivesInput => true;

    public async Task<LiveTestCaseResult> RunAsync(
        LiveTestContext context, CancellationToken cancellationToken)
    {
        context.SetAllowedBlockingPanels(new HashSet<string>(StringComparer.Ordinal)
        {
            "StashElement", "InventoryPanel",
        });
        var baseline = context.Snapshot();
        var stash = baseline.StashInventory;
        var selected = baseline.StashTabs.FindSelected("Maps", false, stash.VisibleTabIndex);
        if (!stash.IsOpen || selected is null)
            return LiveTestCaseResult.Blocked("the selected visible stash tab is not Maps", "PreparedStateMismatch");
        if (!TryTierGrid(baseline, out var grid))
            return LiveTestCaseResult.Blocked("specialized map-stash tier grid was not resolved", "TierGridMissing");

        var gridSnapshot = ElementReader.TryReadSnapshot(baseline.Reader, grid, 32);
        if (gridSnapshot is null || gridSnapshot.Children.Count is < 2 or > 8)
            return LiveTestCaseResult.Blocked("map-stash tier grid row shape is unexpected", "TierGridShapeMismatch");

        var observed = 0;
        var identified = 0;
        for (var row = 0; row < 2; row++)
        {
            var live = context.Snapshot();
            if (!TryTierGrid(live, out grid)
                || !ElementReader.TryGetChild(live.Reader, grid, row, out var rowElement))
                break;
            var rowSnapshot = ElementReader.TryReadSnapshot(live.Reader, rowElement, 32);
            if (rowSnapshot is null) break;
            for (var column = 0; column < rowSnapshot.Children.Count; column++)
            {
                live = context.Snapshot();
                if (!TryTierGrid(live, out grid)
                    || !ElementReader.TryGetChild(live.Reader, grid, row, out rowElement)
                    || !ElementReader.TryGetChild(live.Reader, rowElement, column, out var control)
                    || ElementGeometry.TryReadRect(live.Reader, control) is not { Width: > 12, Height: > 12 } rect)
                    continue;

                var point = live.Window.ToScreen(rect.CenterX, rect.CenterY);
                await context.HoverAsync(point.X, point.Y, 180, cancellationToken);
                var hovered = context.Snapshot();
                hovered.Reader.TryReadStruct<nint>(
                    hovered.IngameStateAddress + KnownOffsets.IngameState.UIHover, out var hover);
                var tooltip = ReadTooltipFromAncestry(hovered.Reader, hover);
                var count = ReadFirstText(hovered.Reader, control);
                context.Observe($"tier control {row},{column}",
                    $"point=({rect.CenterX:F0},{rect.CenterY:F0}) count='{count}' hover=0x{(long)hover:X} tooltip='{tooltip}'");
                observed++;
                if (tooltip.Contains("Tier", StringComparison.OrdinalIgnoreCase)) identified++;
            }
        }

        context.Check(observed >= 16, "map tier controls", $"observed={observed} tooltipIdentified={identified}");
        var mapSelectors = 0;
        if (TryVisibleStash(context.Snapshot(), out var visible))
        {
            var selectorBar = visible;
            foreach (var index in new[] { 3, 0 })
                if (!ElementReader.TryGetChild(context.Snapshot().Reader, selectorBar, index, out selectorBar))
                { selectorBar = 0; break; }
            var selectorSnapshot = selectorBar == 0
                ? null
                : ElementReader.TryReadSnapshot(context.Snapshot().Reader, selectorBar, 256);
            if (selectorSnapshot is not null)
            {
                for (var i = 0; i < selectorSnapshot.Children.Count; i++)
                {
                    var live = context.Snapshot();
                    if (!TryVisibleStash(live, out visible)) break;
                    selectorBar = visible;
                    if (!ElementReader.TryGetChild(live.Reader, selectorBar, 3, out selectorBar)
                        || !ElementReader.TryGetChild(live.Reader, selectorBar, 0, out selectorBar)
                        || !ElementReader.TryGetChild(live.Reader, selectorBar, i, out var control)
                        || !ElementReader.IsVisibleDeep(live.Reader, control)
                        || ElementGeometry.TryReadRect(live.Reader, control) is not { Width: > 12, Height: > 12 } rect)
                        continue;
                    var point = live.Window.ToScreen(rect.CenterX, rect.CenterY);
                    await context.HoverAsync(point.X, point.Y, 220, cancellationToken);
                    var hovered = context.Snapshot();
                    hovered.Reader.TryReadStruct<nint>(
                        hovered.IngameStateAddress + KnownOffsets.IngameState.UIHover, out var hover);
                    var tooltip = ReadTooltipFromAncestry(hovered.Reader, hover);
                    context.Observe($"T16 map selector {i}",
                        $"point=({rect.CenterX:F0},{rect.CenterY:F0}) count='{ReadFirstText(hovered.Reader, control)}' " +
                        $"hover=0x{(long)hover:X} tooltip='{tooltip}' textures='{ReadTextures(hovered.Reader, control)}'");
                    mapSelectors++;
                }
            }
        }
        context.Check(mapSelectors > 0, "visible T16 map selectors", $"observed={mapSelectors}");
        return observed >= 16
            ? LiveTestCaseResult.Pass($"hovered {observed} tier controls and {mapSelectors} visible T16 map selectors", "TierHoverCatalog")
            : LiveTestCaseResult.Fail($"only {observed} tier controls were readable", "TierGridIncomplete");
    }

    private static bool TryTierGrid(GameSnapshot snapshot, out nint grid)
    {
        if (!TryVisibleStash(snapshot, out grid)) return false;
        return true;
    }

    private static bool TryVisibleStash(GameSnapshot snapshot, out nint visible)
    {
        visible = 0;
        if (!snapshot.Reader.TryReadStruct<nint>(
                snapshot.IngameStateAddress + KnownOffsets.IngameState.IngameUi, out var ui)
            || !snapshot.Reader.TryReadStruct<nint>(
                ui + KnownOffsets.IngameUiElements.StashElement, out var stash)
            || !StashReader.TryGetVisibleStash(snapshot.Reader, stash, out visible, out _, out _))
            return false;
        return true;
    }

    private static string ReadTextures(MemoryReader reader, nint root)
    {
        var result = new List<string>();
        var queue = new Queue<(nint Address, int Depth)>();
        var seen = new HashSet<nint>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0 && seen.Count < 64)
        {
            var (address, depth) = queue.Dequeue();
            if (!seen.Add(address)) continue;
            var snapshot = ElementReader.TryReadSnapshot(reader, address, 32);
            if (snapshot is null) continue;
            if (reader.TryReadStruct<nint>(address + KnownOffsets.Element.TextureNamePtr, out var pointer)
                && pointer != 0)
            {
                var value = reader.ReadStringUtf8(pointer, 256);
                if (!string.IsNullOrWhiteSpace(value)) result.Add(value);
            }
            if (depth >= 3) continue;
            foreach (var child in snapshot.Children) queue.Enqueue((child, depth + 1));
        }
        return string.Join(" | ", result.Distinct());
    }

    private static string ReadFirstText(MemoryReader reader, nint root)
    {
        var queue = new Queue<(nint Address, int Depth)>();
        var seen = new HashSet<nint>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0 && seen.Count < 64)
        {
            var (address, depth) = queue.Dequeue();
            if (!seen.Add(address)) continue;
            var snapshot = ElementReader.TryReadSnapshot(reader, address, 32);
            if (snapshot is null) continue;
            var text = NativeString.Read(reader, address + KnownOffsets.Element.TextNoTags);
            if (string.IsNullOrWhiteSpace(text)) text = NativeString.Read(reader, address + KnownOffsets.Element.Text);
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
            if (depth >= 3) continue;
            foreach (var child in snapshot.Children) queue.Enqueue((child, depth + 1));
        }
        return string.Empty;
    }

    private static string ReadTooltipFromAncestry(MemoryReader reader, nint start)
    {
        for (var current = start; current != 0;)
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
                if (string.IsNullOrWhiteSpace(text)) text = NativeString.Read(reader, address + KnownOffsets.Element.Text);
                lines.AddRange(text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
            if (depth >= 10) continue;
            foreach (var child in element.Children) queue.Enqueue((child, depth + 1));
        }
        return string.Join(" || ", lines.Where(line => !string.IsNullOrWhiteSpace(line)).Distinct());
    }
}
