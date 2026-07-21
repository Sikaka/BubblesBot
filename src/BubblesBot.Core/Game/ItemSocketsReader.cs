namespace BubblesBot.Core.Game;

/// <summary>
/// Strict current-build reader for socket colors and link groups. Malformed layouts return false;
/// callers must not hash partial data or silently fall back to "no sockets".
/// </summary>
public static class ItemSocketsReader
{
    public enum SocketColor
    {
        Red = 1,
        Green = 2,
        Blue = 3,
        White = 4,
        Abyss = 5,
        Delve = 6,
    }

    public sealed record Snapshot(
        IReadOnlyList<SocketColor> Colors,
        IReadOnlyList<int> LinkGroupSizes,
        IReadOnlyList<string> Groups,
        IReadOnlyList<SocketedGem> SocketedGems)
    {
        public int SocketCount => Colors.Count;
        public int LargestLinkSize => LinkGroupSizes.Count == 0 ? 0 : LinkGroupSizes.Max();
        public string Canonical =>
            $"colors={string.Concat(Colors.Select(Symbol))};links={string.Join(',', LinkGroupSizes)};groups={string.Join('/', Groups)};" +
            $"gems={string.Join(',', SocketedGems.OrderBy(x => x.SocketIndex).Select(x => x.Canonical))}";
    }

    public sealed record SocketedGem(
        int SocketIndex,
        nint EntityAddress,
        string MetadataPath,
        string BaseName,
        uint Level,
        uint TotalExperience,
        uint PreviousLevelExperience,
        uint NextLevelExperience)
    {
        public string Canonical =>
            $"{SocketIndex}:{MetadataPath}@L{Level}:XP{TotalExperience}:{PreviousLevelExperience}-{NextLevelExperience}";
    }

    public static bool TryRead(MemoryReader reader, nint itemEntity, out Snapshot snapshot)
    {
        snapshot = Empty;
        var components = EntityComponents.ReadComponentMap(reader, itemEntity);
        return components.TryGetValue("Sockets", out var component)
            && TryReadComponent(reader, component, out snapshot);
    }

    public static bool TryReadComponent(MemoryReader reader, nint component, out Snapshot snapshot)
    {
        snapshot = Empty;
        if (!LooksLikeUserAddress(component)) return false;

        var colors = new List<SocketColor>(KnownOffsets.SocketsComponent.MaximumSockets);
        var terminated = false;
        for (var index = 0; index < KnownOffsets.SocketsComponent.MaximumSockets; index++)
        {
            if (!reader.TryReadStruct<int>(component + KnownOffsets.SocketsComponent.Colors + index * 4, out var raw))
                return false;
            if (raw == 0)
            {
                terminated = true;
                continue;
            }
            if (terminated || raw is < 1 or > 6)
                return false;
            colors.Add((SocketColor)raw);
        }

        if (!reader.TryReadStruct<StdVector>(component + KnownOffsets.SocketsComponent.LinkSizes, out var links))
            return false;
        var bytes = links.ByteCount;
        if (colors.Count == 0)
        {
            if (bytes != 0) return false;
            snapshot = Empty;
            return true;
        }
        if (!LooksLikeUserAddress(links.First) || links.Last < links.First || links.End < links.Last
            || bytes is < 1 or > KnownOffsets.SocketsComponent.MaximumSockets)
            return false;

        var rawGroups = new byte[(int)bytes];
        if (reader.TryReadBytes(links.First, rawGroups) != rawGroups.Length
            || rawGroups.Any(x => x is < 1 or > KnownOffsets.SocketsComponent.MaximumSockets)
            || rawGroups.Sum(x => (int)x) != colors.Count)
            return false;

        var sizes = rawGroups.Select(x => (int)x).ToArray();
        var groups = new List<string>(sizes.Length);
        var cursor = 0;
        foreach (var size in sizes)
        {
            groups.Add(string.Concat(colors.Skip(cursor).Take(size).Select(Symbol)));
            cursor += size;
        }
        var gems = new List<SocketedGem>();
        for (var index = 0; index < KnownOffsets.SocketsComponent.MaximumSockets; index++)
        {
            if (!reader.TryReadStruct<nint>(
                    component + KnownOffsets.SocketsComponent.SocketedGems + index * 8, out var gemEntity))
                return false;
            if (index >= colors.Count)
            {
                if (gemEntity != 0) return false;
                continue;
            }
            if (gemEntity == 0) continue;
            if (!LooksLikeUserAddress(gemEntity)) return false;
            if (!SkillGemReader.TryRead(reader, gemEntity, out var gem)) return false;
            gems.Add(new SocketedGem(index, gemEntity, gem.MetadataPath, gem.BaseName,
                gem.Level, gem.TotalExperience, gem.PreviousLevelExperience, gem.NextLevelExperience));
        }

        snapshot = new Snapshot(colors, sizes, groups, gems);
        return true;
    }

    private static readonly Snapshot Empty = new([], [], [], []);

    private static string Symbol(SocketColor color) => color switch
    {
        SocketColor.Red => "R",
        SocketColor.Green => "G",
        SocketColor.Blue => "B",
        SocketColor.White => "W",
        SocketColor.Abyss => "A",
        SocketColor.Delve => "D",
        _ => "?",
    };

    private static bool LooksLikeUserAddress(nint address)
        => (long)address is > 0x10000 and < 0x7FFF_FFFF_FFFF;
}
