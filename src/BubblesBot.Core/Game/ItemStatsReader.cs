namespace BubblesBot.Core.Game;

/// <summary>
/// Reads an item entity's flattened stat records — <c>(int32 statId, int32 value)</c> pairs,
/// sorted ascending by id — from its Mods component (decoded 2026-07-14, see
/// <c>KnownOffsets.ItemStatRecord</c>). Values are effective totals aggregated across the
/// item's mods; "reduced X" stats are negative. Lenient runtime read: absent or malformed data
/// returns an empty list — strict structural validation is the <c>item.mods</c> probe's job.
/// Pair with <c>Knowledge/MapStatCatalog</c> to score and veto map items.
/// </summary>
public static class ItemStatsReader
{
    public static IReadOnlyList<(int Id, int Value)> Read(MemoryReader reader, nint itemEntity)
    {
        var comps = EntityComponents.ReadComponentMap(reader, itemEntity);
        return comps.TryGetValue("Mods", out var modsAddr)
            ? ReadFromModsComponent(reader, modsAddr)
            : [];
    }

    public static IReadOnlyList<(int Id, int Value)> ReadFromModsComponent(MemoryReader reader, nint modsComponent)
    {
        if (!reader.TryReadStruct<ModsComponent>(modsComponent, out var mc)) return [];
        var bytes = (long)mc.ItemStats.Last - (long)mc.ItemStats.First;
        if (bytes <= 0 || bytes > 64 * KnownOffsets.ItemStatRecord.Stride || bytes % KnownOffsets.ItemStatRecord.Stride != 0)
            return [];

        var n = (int)(bytes / KnownOffsets.ItemStatRecord.Stride);
        var list = new List<(int, int)>(n);
        for (var i = 0; i < n; i++)
        {
            var rec = mc.ItemStats.First + i * KnownOffsets.ItemStatRecord.Stride;
            if (!reader.TryReadStruct<int>(rec + KnownOffsets.ItemStatRecord.Id, out var id)
                || !reader.TryReadStruct<int>(rec + KnownOffsets.ItemStatRecord.Value, out var value))
                return [];
            list.Add((id, value));
        }
        return list;
    }
}
