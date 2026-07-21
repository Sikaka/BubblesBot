using BubblesBot.Bot.Input;
using BubblesBot.Core;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>
/// Reversible discovery of numbered map-stash subinventories. Each click is confined to a
/// structurally resolved selector and confirmed by the exact visible subinventory index.
/// </summary>
public sealed class MapStashSelectorRoundTripLiveTest : ILiveTestCase
{
    public string Id => "H-08-map-stash-selectors";
    public string Name => "Map stash selector round trip";
    public string Description => "Selects each visible numbered T16 map-stash subinventory, records item rarities, and restores the original selector.";
    public string ManualSetup => "Open the active Maps stash tab on T16 and inventory; do not move panels during the test.";
    public LiveTestMutation Mutation => LiveTestMutation.Reversible;
    public bool DrivesInput => true;

    public async Task<LiveTestCaseResult> RunAsync(
        LiveTestContext context, CancellationToken cancellationToken)
    {
        context.SetAllowedBlockingPanels(new HashSet<string>(StringComparer.Ordinal)
        {
            "StashElement", "InventoryPanel",
        });
        var initial = context.Snapshot();
        if (!initial.StashInventory.IsOpen
            || initial.StashTabs.FindSelected("Maps", false, initial.StashInventory.VisibleTabIndex) is null
            || !TryVisiblePanelIndex(initial, out var originalPanel))
            return LiveTestCaseResult.Blocked("Maps/T16 specialized stash state is not readable", "PreparedStateMismatch");

        var selectors = ReadSelectors(initial);
        context.Check(selectors.Count >= 2, "numbered map selectors",
            string.Join(", ", selectors.Select(x => $"{x.Label}@{x.Index}")));
        if (selectors.Count < 2)
            return LiveTestCaseResult.Blocked("fewer than two numbered map selectors are visible", "SelectorShapeMismatch");

        var normalPanels = new List<int>();
        foreach (var selector in selectors.OrderBy(x => int.Parse(x.Label)))
        {
            var expectedPanel = int.Parse(selector.Label) - 1;
            if (expectedPanel == originalPanel)
            {
                ObservePanel(context, expectedPanel, normalPanels);
                continue;
            }

            var live = context.Snapshot();
            var current = ReadSelectors(live).FirstOrDefault(x => x.Label == selector.Label);
            if (current is null) continue;
            var point = live.Window.ToScreen(current.Rect.CenterX, current.Rect.CenterY);
            var outcome = await context.VerifiedClickAsync(
                point.X, point.Y, ClickIntent.InteractUi,
                $"select map-stash subinventory {selector.Label}",
                () => TryVisiblePanelIndex(context.Snapshot(), out var index) && index == expectedPanel,
                1_800, cancellationToken);
            if (outcome != ActionOutcome.Confirmed)
                return await RestoreThenFail(context, originalPanel,
                    $"selector {selector.Label} did not activate panel {expectedPanel}", cancellationToken);
            await Task.Delay(250, cancellationToken);
            ObservePanel(context, expectedPanel, normalPanels);
        }

        if (!await Restore(context, originalPanel, cancellationToken))
            return LiveTestCaseResult.Fail("selector catalog completed but original map subinventory was not restored", "RestoreFailed");
        context.Check(true, "map selector restoration", $"panel={originalPanel}");
        return LiveTestCaseResult.Pass(
            $"catalogued {selectors.Count} T16 subinventories; Normal-rarity keys in panels [{string.Join(',', normalPanels.Distinct().Order())}]",
            "SelectorCatalogued");
    }

    private static void ObservePanel(LiveTestContext context, int panel, List<int> normalPanels)
    {
        var stash = context.Snapshot().StashInventory;
        var maps = stash.Items.Where(item => InventoryView.IsMap(new InventoryView.Item(
            item.ElementAddress, item.ItemEntity, item.Rect, item.Path, item.StackSize,
            item.Width, item.Height, item.Stats, item.BaseName, item.Rarity, item.Quality))).ToArray();
        var normal = maps.Count(item => StashInventoryView.IsNormalTierMap(item, 16));
        if (normal > 0) normalPanels.Add(panel);
        context.Observe($"map subinventory {panel}",
            $"items={maps.Length} normalT16={normal} rarities=[{string.Join(',', maps.GroupBy(x => x.Rarity).Select(g => $"{g.Key}:{g.Count()}"))}]");
    }

    private static async Task<LiveTestCaseResult> RestoreThenFail(
        LiveTestContext context, int originalPanel, string reason, CancellationToken cancellationToken)
    {
        var restored = await Restore(context, originalPanel, cancellationToken);
        return LiveTestCaseResult.Fail(
            restored ? reason : $"{reason}; original panel restoration also failed",
            restored ? "SelectionMismatch" : "RestoreFailed");
    }

    private static async Task<bool> Restore(
        LiveTestContext context, int originalPanel, CancellationToken cancellationToken)
    {
        if (TryVisiblePanelIndex(context.Snapshot(), out var current) && current == originalPanel)
            return true;
        var label = (originalPanel + 1).ToString();
        var live = context.Snapshot();
        var selector = ReadSelectors(live).FirstOrDefault(x => x.Label == label);
        if (selector is null) return false;
        var point = live.Window.ToScreen(selector.Rect.CenterX, selector.Rect.CenterY);
        var outcome = await context.VerifiedClickAsync(
            point.X, point.Y, ClickIntent.InteractUi,
            $"restore map-stash subinventory {label}",
            () => TryVisiblePanelIndex(context.Snapshot(), out var index) && index == originalPanel,
            1_800, cancellationToken);
        return outcome == ActionOutcome.Confirmed;
    }

    private static IReadOnlyList<Selector> ReadSelectors(GameSnapshot snapshot)
    {
        if (!TryVisibleStash(snapshot, out var visible)) return [];
        var bar = visible;
        foreach (var index in new[] { 3, 0 })
            if (!ElementReader.TryGetChild(snapshot.Reader, bar, index, out bar)) return [];
        var barSnapshot = ElementReader.TryReadSnapshot(snapshot.Reader, bar, 256);
        if (barSnapshot is null) return [];
        var result = new List<Selector>();
        for (var i = 0; i < barSnapshot.Children.Count; i++)
        {
            var control = barSnapshot.Children[i];
            if (!ElementReader.IsVisibleDeep(snapshot.Reader, control)
                || ElementGeometry.TryReadRect(snapshot.Reader, control) is not { Width: > 20, Width: < 100, Height: > 15 } rect)
                continue;
            var label = ReadFirstText(snapshot.Reader, control);
            if (int.TryParse(label, out var number) && number is >= 1 and <= 99)
                result.Add(new Selector(i, label, rect));
        }
        return result;
    }

    private static bool TryVisiblePanelIndex(GameSnapshot snapshot, out int index)
    {
        index = -1;
        if (!TryVisibleStash(snapshot, out var visible)
            || !ElementReader.TryGetChild(snapshot.Reader, visible, 3, out var root)
            || !ElementReader.TryGetChild(snapshot.Reader, root, 1, out var panels))
            return false;
        var snapshotPanels = ElementReader.TryReadSnapshot(snapshot.Reader, panels, 256);
        if (snapshotPanels is null) return false;
        for (var i = 0; i < snapshotPanels.Children.Count; i++)
            if (ElementReader.IsVisibleDeep(snapshot.Reader, snapshotPanels.Children[i]))
            { index = i; return true; }
        return false;
    }

    private static bool TryVisibleStash(GameSnapshot snapshot, out nint visible)
    {
        visible = 0;
        return snapshot.Reader.TryReadStruct<nint>(
                   snapshot.IngameStateAddress + KnownOffsets.IngameState.IngameUi, out var ui)
               && snapshot.Reader.TryReadStruct<nint>(
                   ui + KnownOffsets.IngameUiElements.StashElement, out var stash)
               && StashReader.TryGetVisibleStash(snapshot.Reader, stash, out visible, out _, out _);
    }

    private static string ReadFirstText(MemoryReader reader, nint root)
    {
        var queue = new Queue<(nint Address, int Depth)>();
        var seen = new HashSet<nint>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0 && seen.Count < 32)
        {
            var (address, depth) = queue.Dequeue();
            if (!seen.Add(address)) continue;
            var snapshot = ElementReader.TryReadSnapshot(reader, address, 8);
            if (snapshot is null) continue;
            var text = NativeString.Read(reader, address + KnownOffsets.Element.TextNoTags);
            if (string.IsNullOrWhiteSpace(text)) text = NativeString.Read(reader, address + KnownOffsets.Element.Text);
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
            if (depth >= 3) continue;
            foreach (var child in snapshot.Children) queue.Enqueue((child, depth + 1));
        }
        return string.Empty;
    }

    private sealed record Selector(int Index, string Label, ElementGeometry.Rect Rect);
}
