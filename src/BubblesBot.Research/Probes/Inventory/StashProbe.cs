using BubblesBot.Core.Game;
using BubblesBot.Research.Probing;

namespace BubblesBot.Research.Probes.Inventory;

/// <summary>
/// Stash element: total tab count, visible-tab index, and the visible-stash inventory pointer.
/// Migrated from VisibleStashRootOracleTest. SKIPs when the stash is closed.
/// </summary>
public sealed class StashProbe : IProbe
{
    public string Name => "inventory.stash";
    public string Group => "inventory";
    public string Description => "StashElement total/index/visible-stash resolve.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx)
    {
        var ui = ctx.Chain.IngameUi;
        if (ui == 0) return ProbeResult.Fail("IngameUi null");
        if (!ctx.Reader.TryReadStruct<nint>(ui + KnownOffsets.IngameUiElements.StashElement, out var stash) || stash == 0)
            return ProbeResult.Skip("StashElement null (stash closed?)");

        if (!StashReader.TryGetVisibleStash(ctx.Reader, stash, out var visible, out var index, out var total))
            return ProbeResult.Skip("stash inventory panel not visible/resolved");

        var totalR = Check.Live(ctx, "stash.total", total, "StashElement.TotalStashes", 1, 1000);
        var indexR = Check.Live(ctx, "stash.index", index, "StashElement.IndexVisibleStash", 0, Math.Max(0, total - 1));
        var visR = Check.Address(ctx, "stash.visible", visible, "StashElement.VisibleStash", requireNonNull: true, a => Reads.Readable(ctx.Reader, a));

        return ProbeResult.Combine(totalR, indexR, visR);
    }

    public ProbeResult Discover(ProbeContext ctx)
    {
        var ui = ctx.Chain.IngameUi;
        if (ui == 0 || !ctx.Reader.TryReadStruct<nint>(ui + KnownOffsets.IngameUiElements.StashElement, out var stash) || stash == 0)
            return ProbeResult.Found("StashElement.VisibleStash", []);
        return Discovery.Pointer(ctx, stash, "stash.visible", 0x800, "StashElement.VisibleStash");
    }
}
