using BubblesBot.Core.Game;
using BubblesBot.Research.Probing;
using BubblesBot.Research.Probing.Toolkit;

namespace BubblesBot.Research.Probes.Ui;

/// <summary>
/// Validates the Element layout itself against the live UIRoot: the self-pointer is self-referential
/// (proves Element.SelfPointer) and the children array has a plausible count (proves Element.Childs).
/// Structural — needs neither baseline nor oracle.
/// </summary>
public sealed class UiRootElementProbe : IProbe
{
    public string Name => "ui.element-layout";
    public string Group => "ui";
    public string Description => "Element.SelfPointer + Element.Childs valid on UIRoot.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx)
    {
        var root = ctx.Chain.UiRoot;
        if (root == 0) return ProbeResult.Fail("UIRoot null");

        var self = ProbeResult.Fail("Element.SelfPointer mismatch");
        if (ctx.Reader.TryReadStruct<nint>(root + KnownOffsets.Element.SelfPointer, out var s))
            self = s == root
                ? ProbeResult.Pass($"Element.SelfPointer@+0x{KnownOffsets.Element.SelfPointer:X} self-referential")
                : ProbeResult.Fail($"Element.SelfPointer@+0x{KnownOffsets.Element.SelfPointer:X}: 0x{(long)s:X} != self");

        var childs = ProbeResult.Fail("Element.Childs unreadable");
        if (ctx.Reader.TryReadStruct<NativePtrArray>(root + KnownOffsets.Element.Childs, out var arr))
            childs = arr.Count is > 0 and <= 10_000
                ? ProbeResult.Pass($"Element.Childs@+0x{KnownOffsets.Element.Childs:X} count={arr.Count}")
                : ProbeResult.Fail($"Element.Childs count implausible ({arr.Count})");

        return ProbeResult.Combine(self, childs);
    }

    public ProbeResult Discover(ProbeContext ctx)
    {
        var root = ctx.Chain.UiRoot;
        if (root == 0) return ProbeResult.Found("Element.SelfPointer", []);
        // The self-pointer is the value == base; scan the element header for a slot holding `root`.
        var cands = MemScan.WindowPtr(ctx.Reader, root, 0x200, root)
            .Select(o => new OffsetCandidate(o, "self-referential slot (Element.SelfPointer)"));
        return ProbeResult.Found("Element.SelfPointer", cands);
    }
}
