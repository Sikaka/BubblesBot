namespace BubblesBot.Core.Game;

public static class ElementReader
{
    public sealed record ElementSnapshot(
        nint Address,
        bool IsValid,
        bool IsVisibleLocal,
        Vector2 Position,
        Vector2 Size,
        float Scale,
        nint Parent,
        IReadOnlyList<nint> Children);

    public static ElementSnapshot? TryReadSnapshot(MemoryReader reader, nint elementAddress, int maxChildren = 10_000)
    {
        if (!LooksLikeUserAddress(elementAddress))
            return null;
        if (!reader.TryReadStruct<Element>(elementAddress, out var element))
            return null;

        var children = ReadChildren(reader, element.Childs, maxChildren);
        // IsVisibleLocal is bit 11 (0x800) of Flags in modern ExileCore — not bit 2 (0x04)
        // as old ExileApi used. Validated 2026-05-05.
        return new ElementSnapshot(
            elementAddress,
            element.SelfPointer == elementAddress,
            (element.Flags & 0x800) == 0x800,
            element.Position,
            element.Size,
            element.Scale,
            element.Parent,
            children);
    }

    /// <summary>
    /// ExileCore-equivalent <c>Element.IsVisible</c>: the element AND every ancestor up to the
    /// UI root must have the visible bit (Flags &amp; 0x800) set. Returns true once the walk
    /// reaches the root cleanly (parent == 0 or self-loop) without ever seeing a cleared bit;
    /// false on any cleared bit or unreadable link. This is the canonical "would the player
    /// actually see it" test — a panel pointer can stay allocated while the panel is hidden,
    /// so pointer-non-null alone is not an open signal.
    ///
    /// <para>Validated 2026-07-13 against ExileCore's own IsVisible: opening inventory flips
    /// bit 0x800 on the panel element (010C26F1 → 010C2EF1). The UIRoot's parent pointer is
    /// 0, so the walk MUST treat reaching a null parent as "root reached / visible", not as
    /// failure — an earlier `return addr != 0` bug reported every fully-visible panel as
    /// hidden because the last step walked into the null root.</para>
    /// </summary>
    public static bool IsVisibleDeep(MemoryReader reader, nint elementAddress, int maxDepth = 32)
    {
        const uint visibleBit = 0x800;
        var addr = elementAddress;
        if (addr == 0) return false;
        for (var d = 0; d < maxDepth; d++)
        {
            if (!reader.TryReadStruct<uint>(addr + KnownOffsets.Element.Flags, out var flags)) return false;
            if ((flags & visibleBit) == 0) return false;
            if (!reader.TryReadStruct<nint>(addr + KnownOffsets.Element.Parent, out var parent)) return false;
            if (parent == 0 || parent == addr) return true;  // reached UI root with all bits set
            addr = parent;
        }
        return true;  // hit depth cap with an unbroken chain of set bits — treat as visible
    }

    /// <summary>
    /// Read only this element's rendered bit. Ground-item labels live under a virtualized
    /// container whose ancestor visibility does not reliably match ExileCore's per-label
    /// <c>Label.IsVisible</c>; their clickability is represented by the label element itself.
    /// Panels should continue to use <see cref="IsVisibleDeep"/>.
    /// </summary>
    public static bool IsVisibleLocal(MemoryReader reader, nint elementAddress)
        => elementAddress != 0
        && reader.TryReadStruct<uint>(elementAddress + KnownOffsets.Element.Flags, out var flags)
        && (flags & 0x800) == 0x800;

    public static bool TryGetChild(MemoryReader reader, nint elementAddress, int index, out nint childAddress)
    {
        childAddress = 0;
        if (index < 0 || !LooksLikeUserAddress(elementAddress))
            return false;
        if (!reader.TryReadStruct<Element>(elementAddress, out var element))
            return false;
        if (index >= element.Childs.Count)
            return false;
        return reader.TryReadStruct(element.Childs.First + index * 8, out childAddress)
            && LooksLikeUserAddress(childAddress);
    }

    private static IReadOnlyList<nint> ReadChildren(MemoryReader reader, NativePtrArray childArray, int maxChildren)
    {
        var count = childArray.Count;
        if (count <= 0 || count > maxChildren)
            return Array.Empty<nint>();

        var result = new nint[count];
        for (var i = 0; i < count; i++)
        {
            if (!reader.TryReadStruct<nint>(childArray.First + i * 8, out result[i]))
                return Array.Empty<nint>();
        }

        return result;
    }

    private static bool LooksLikeUserAddress(nint p)
    {
        var v = (long)p;
        return v > 0x10000 && v < 0x7FFF_FFFF_FFFF;
    }
}
