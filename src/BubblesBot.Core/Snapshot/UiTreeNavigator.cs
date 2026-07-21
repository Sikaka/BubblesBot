using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Resolves an <see cref="UiIndexPath"/> at runtime by walking <c>Element.Children[i]</c>
/// pointers from a starting element. Cheap — N pointer reads per resolve, where N is the
/// path length (typically 2-5). The address result is good for the panel's open lifetime;
/// callers cache.
/// </summary>
public static class UiTreeNavigator
{
    /// <summary>
    /// Walk <paramref name="path"/> from <paramref name="rootElementAddress"/> and return
    /// the element address at the end of the path. Returns 0 on any read failure or
    /// out-of-range index — the typical "panel isn't open" or "index path drifted" cases.
    /// </summary>
    public static nint Resolve(MemoryReader reader, nint rootElementAddress, UiIndexPath path)
    {
        if (rootElementAddress == 0) return 0;
        var current = rootElementAddress;
        foreach (var idx in path.Indices)
        {
            current = ChildAt(reader, current, idx);
            if (current == 0) return 0;
        }
        return current;
    }

    /// <summary>Read the address of <c>parent.Children[index]</c>. Returns 0 on failure / out of range.</summary>
    public static nint ChildAt(MemoryReader reader, nint parentElementAddress, int index)
    {
        if (parentElementAddress == 0 || index < 0) return 0;
        if (!reader.TryReadStruct<nint>(parentElementAddress + KnownOffsets.Element.Childs, out var childsBegin) || childsBegin == 0) return 0;
        if (!reader.TryReadStruct<nint>(parentElementAddress + KnownOffsets.Element.Childs + 8, out var childsEnd)) return 0;
        var byteLen = (long)childsEnd - (long)childsBegin;
        if (byteLen <= 0 || byteLen > 8 * 1024) return 0;          // sanity cap on child count
        var count = (int)(byteLen / 8);
        if (index >= count) return 0;
        if (!reader.TryReadStruct<nint>(childsBegin + index * 8, out var child)) return 0;
        return child;
    }

    /// <summary>Get the live child count of an element. 0 on failure.</summary>
    public static int ChildCount(MemoryReader reader, nint elementAddress)
    {
        if (elementAddress == 0) return 0;
        if (!reader.TryReadStruct<nint>(elementAddress + KnownOffsets.Element.Childs, out var begin) || begin == 0) return 0;
        if (!reader.TryReadStruct<nint>(elementAddress + KnownOffsets.Element.Childs + 8, out var end)) return 0;
        var byteLen = (long)end - (long)begin;
        if (byteLen <= 0 || byteLen > 8 * 1024) return 0;
        return (int)(byteLen / 8);
    }
}
