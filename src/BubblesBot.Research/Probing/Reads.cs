using BubblesBot.Core;
using BubblesBot.Core.Game;

namespace BubblesBot.Research.Probing;

/// <summary>Tiny shared read predicates probes use for structural soundness checks.</summary>
public static class Reads
{
    /// <summary>Non-null and the first qword is readable.</summary>
    public static bool Readable(MemoryReader r, nint addr)
        => addr != 0 && r.TryReadStruct<long>(addr, out _);

    /// <summary>A real UiElement is self-referential at +SelfPointer (prunes garbage pointers).</summary>
    public static bool IsElement(MemoryReader r, nint addr)
        => addr != 0
        && r.TryReadStruct<nint>(addr + KnownOffsets.Element.SelfPointer, out var self)
        && self == addr;
}
