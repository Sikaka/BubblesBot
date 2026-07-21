using BubblesBot.Core.Snapshot;
using BubblesBot.Research.Probing;

namespace BubblesBot.Research.Probes.Inventory;

public sealed class StashItemsProbe : IProbe
{
    public string Name => "inventory.stash-items";
    public string Group => "inventory";
    public string Description => "Visible stash-tab item entities, stacks, and click rectangles.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx)
    {
        var view = StashInventoryView.FromIngameUi(ctx.Reader, ctx.Chain.IngameState);
        if (!view.IsOpen) return ProbeResult.Skip("stash is closed");
        var rows = view.Items.Select(item =>
            $"{item.Path.Split('/').LastOrDefault()} stack={item.StackSize} "
            + $"size={item.Width}x{item.Height} "
            + $"rect={(item.Rect is { } r ? $"{r.X:F0},{r.Y:F0},{r.Width:F0},{r.Height:F0}" : "?")}");
        return ProbeResult.Pass(
            $"tab={view.VisibleTabIndex}/{view.TotalTabs} items={view.Items.Count}: "
            + string.Join(" | ", rows));
    }

    public ProbeResult Discover(ProbeContext ctx)
        => ProbeResult.Found("visible stash item widgets", []);

}
