using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;
using BubblesBot.Research.Probing;

namespace BubblesBot.Research.Probes.Inventory;

/// <summary>Read-only capture of exact visible stash-tab labels and current hover ancestry.</summary>
public sealed class StashTabStripProbe : IProbe
{
    public string Name => "inventory.stash-tab-strip";
    public string Group => "inventory";
    public string Description => "Visible stash-tab label controls, geometry, and current UI-hover ownership.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx)
    {
        var strip = StashTabStripView.FromIngameUi(ctx.Reader, ctx.Chain.IngameState);
        var hover = UiHoverView.Read(ctx.Reader, ctx.Chain.IngameState).Element;
        if (strip.Controls.Count == 0) return ProbeResult.Skip("stash tab strip is closed or unreadable");
        var rows = strip.Controls.Select(control =>
            $"'{control.Text}' element=0x{control.Element:X} rect={control.Rect} ownsHover={Owns(ctx, control.Element, hover)}");
        return ProbeResult.Pass($"hover=0x{hover:X} controls={strip.Controls.Count}: " + string.Join(" | ", rows));
    }

    public ProbeResult Discover(ProbeContext ctx) => ProbeResult.Found("stash tab strip", []);

    private static bool Owns(ProbeContext ctx, nint target, nint current)
    {
        for (var depth = 0; depth < 24 && current != 0; depth++)
        {
            if (current == target) return true;
            if (!ctx.Reader.TryReadStruct<nint>(current + KnownOffsets.Element.Parent, out var parent)
                || parent == current) break;
            current = parent;
        }
        return false;
    }
}
