namespace BubblesBot.Core.Game;

/// <summary>
/// Reads PoE's per-entity component map. Each Entity has:
///   - Entity + 0x10: StdVector "ComponentList" — array of component instance pointers (one per component).
///   - Entity + 0x8: pointer to a shared "details" / type object whose layout is KnownOffsets.ObjectHeader.
///     ObjectHeader + 0x28 points to a ComponentLookUpStruct:
///       - + 0x28: StdVector of ComponentNameAndIndexStruct entries (size 0x10 each: NamePtr@0, Index@8).
///   - To resolve "Life" -> component instance: look up "Life" in the name array, take its Index, then index into ComponentList.
/// </summary>
public static class EntityComponents
{
    public readonly record struct ComponentNameAndIndex(string Name, int Index, nint Address);

    /// <summary>
    /// Read the full component map for an entity. Returns name -> component instance address.
    /// Returns empty dictionary if the entity is invalid / not yet populated. Every pointer
    /// hop uses TryReadStruct + range validation, so stale entity memory (post-area-change,
    /// mid-respawn) returns an empty map instead of throwing.
    /// </summary>
    public static Dictionary<string, nint> ReadComponentMap(MemoryReader reader, nint entityAddress)
    {
        var map = new Dictionary<string, nint>(StringComparer.Ordinal);
        if (!LooksLikeUserAddress(entityAddress)) return map;

        if (!reader.TryReadStruct<nint>(entityAddress + KnownOffsets.Entity.EntityDetailsPtr, out var entityDetails)
            || !LooksLikeUserAddress(entityDetails))
            return map;

        if (!reader.TryReadStruct<nint>(entityDetails + KnownOffsets.ObjectHeader.ComponentLookUpPtr, out var componentLookUp)
            || !LooksLikeUserAddress(componentLookUp))
            return map;

        var nameArrayAddr = componentLookUp + KnownOffsets.ComponentLookUp.ComponentArray;
        if (!reader.TryReadStruct<StdVector>(nameArrayAddr, out var nameArray)) return map;
        if (!LooksLikeUserAddress(nameArray.First)) return map;

        var componentListAddr = entityAddress + KnownOffsets.Entity.ComponentList;
        if (!reader.TryReadStruct<StdVector>(componentListAddr, out var componentList)) return map;
        if (!LooksLikeUserAddress(componentList.First)) return map;
        var componentCount = (int)(((long)componentList.Last - (long)componentList.First) / sizeof(long));
        if (componentCount <= 0 || componentCount > 4096) return map;

        const int nameEntrySize = 0x10; // NamePtr@0 + Index@8 + 4 bytes pad
        var nameEntryCount = (int)(((long)nameArray.Last - (long)nameArray.First) / nameEntrySize);
        if (nameEntryCount <= 0 || nameEntryCount > 4096) return map;

        for (var i = 0; i < nameEntryCount; i++)
        {
            var entryAddr = nameArray.First + (nint)(i * nameEntrySize);
            if (!reader.TryReadStruct<nint>(entryAddr, out var namePtr)) continue;
            if (!reader.TryReadStruct<int>(entryAddr + 0x8, out var index)) continue;
            if (!LooksLikeUserAddress(namePtr) || index < 0 || index >= componentCount) continue;

            var name = reader.ReadStringUtf8(namePtr, 64);
            if (string.IsNullOrEmpty(name)) continue;

            if (!reader.TryReadStruct<nint>(componentList.First + (nint)(index * sizeof(long)), out var compAddr)) continue;
            if (LooksLikeUserAddress(compAddr)) map[name] = compAddr;
        }

        return map;
    }

    private static bool LooksLikeUserAddress(nint p)
    {
        var v = (long)p;
        return v > 0x10000 && v < 0x7FFF_FFFF_FFFF;
    }
}
