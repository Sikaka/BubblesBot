using BubblesBot.Core;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;
using BubblesBot.Research.Probing;

namespace BubblesBot.Research.Probes.Inventory;

/// <summary>
/// Read-only: reports the stash root-tab layout — every strip label in visual (left-to-right)
/// order, and which one is currently active. Active tab = the strip index stored at
/// StashElement+0x198. No input; safe to run any time the stash is open. SKIPs when closed.
/// </summary>
public sealed class StashActiveTabNameProbe : IProbe
{
    private static readonly int[] StripPath = { 2, 0, 0, 1, 0 };
    private const int SelectedTabOffset = 0x198;   // StashElement+0x198 = active root-tab strip index
    private const uint OnScreenFlag = 0x0800;       // Element.Flags bit set while rendered

    public string Name => "inventory.stash-tabs-ordered";
    public string Group => "inventory";
    public string Description => "Root-tab strip in visual order + the active tab (StashElement+0x198). Read-only.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx)
    {
        var ui = ctx.Chain.IngameUi;
        if (ui == 0) return ProbeResult.Fail("IngameUi null");
        if (!ctx.Reader.TryReadStruct<nint>(ui + KnownOffsets.IngameUiElements.StashElement, out var stash) || stash == 0)
            return ProbeResult.Skip("StashElement null (stash closed?)");

        ctx.Reader.TryReadStruct<int>(stash + SelectedTabOffset, out var activeIndex);

        var strip = stash;
        foreach (var i in StripPath)
            if (!ElementReader.TryGetChild(ctx.Reader, strip, i, out strip))
                return ProbeResult.Fail("tab strip path did not resolve");
        var snap = ElementReader.TryReadSnapshot(ctx.Reader, strip, 1000);
        if (snap is null) return ProbeResult.Fail("strip snapshot null");

        var tabs = new List<(int Index, string Name, float X, bool OnScreen)>();
        for (var i = 0; i < snap.Children.Count; i++)
        {
            var e = snap.Children[i];
            var name = DeepText(ctx.Reader, e);
            if (string.IsNullOrWhiteSpace(name)) continue;
            var x = ElementGeometry.TryReadRect(ctx.Reader, e) is { } r ? r.X : float.NaN;
            var onScreen = ctx.Reader.TryReadStruct<uint>(e + KnownOffsets.Element.Flags, out var f) && (f & OnScreenFlag) != 0;
            tabs.Add((i, name, x, onScreen));
        }

        var activeName = tabs.FirstOrDefault(t => t.Index == activeIndex).Name ?? "(none)";
        var ordered = tabs.OrderBy(t => float.IsNaN(t.X) ? float.MaxValue : t.X)
            .Select(t => $"{(t.Index == activeIndex ? "*" : "")}{t.Index}:'{t.Name}'@{(float.IsNaN(t.X) ? "off" : ((int)t.X).ToString())}{(t.OnScreen ? "" : "(hidden)")}");

        return ProbeResult.Pass(
            $"active +0x198={activeIndex} name='{activeName}' | {tabs.Count} labels in visual order: "
            + string.Join(" ", ordered));
    }

    public ProbeResult Discover(ProbeContext ctx) => ProbeResult.Found("stash tabs ordered", []);

    private static string DeepText(MemoryReader reader, nint root)
    {
        var queue = new Queue<(nint Address, int Depth)>();
        var seen = new HashSet<nint>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0 && seen.Count < 100)
        {
            var (address, depth) = queue.Dequeue();
            if (!seen.Add(address)) continue;
            var snap = ElementReader.TryReadSnapshot(reader, address, 64);
            if (snap is null) continue;
            var text = NativeString.Read(reader, address + KnownOffsets.Element.TextNoTags);
            if (string.IsNullOrWhiteSpace(text)) text = NativeString.Read(reader, address + KnownOffsets.Element.Text);
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
            if (depth >= 4) continue;
            foreach (var child in snap.Children) queue.Enqueue((child, depth + 1));
        }
        return string.Empty;
    }
}
