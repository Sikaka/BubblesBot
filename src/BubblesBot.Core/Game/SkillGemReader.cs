namespace BubblesBot.Core.Game;

/// <summary>Strict current-build state reader shared by loose and socketed skill gems.</summary>
public static class SkillGemReader
{
    public sealed record Snapshot(
        string MetadataPath,
        string BaseName,
        uint Level,
        uint TotalExperience,
        uint PreviousLevelExperience,
        uint NextLevelExperience)
    {
        public string Canonical =>
            $"{MetadataPath}@L{Level}:XP{TotalExperience}:{PreviousLevelExperience}-{NextLevelExperience}";
    }

    public static bool TryRead(MemoryReader reader, nint gemEntity, out Snapshot snapshot)
    {
        snapshot = Empty;
        if (!LooksLikeUserAddress(gemEntity)) return false;

        var path = EntityListReader.ReadEntityPath(reader, gemEntity) ?? string.Empty;
        if (!path.StartsWith("Metadata/Items/Gems/", StringComparison.Ordinal)) return false;

        var components = EntityComponents.ReadComponentMap(reader, gemEntity);
        if (!components.TryGetValue("SkillGem", out var skillGem)
            || !reader.TryReadStruct<uint>(skillGem + KnownOffsets.SkillGemComponent.Level, out var level)
            || !reader.TryReadStruct<uint>(skillGem + KnownOffsets.SkillGemComponent.TotalExpGained, out var totalExperience)
            || !reader.TryReadStruct<uint>(skillGem + KnownOffsets.SkillGemComponent.ExperiencePrevLevel, out var previousExperience)
            || !reader.TryReadStruct<uint>(skillGem + KnownOffsets.SkillGemComponent.ExperienceMaxLevel, out var nextExperience)
            || level is < 1 or > 40
            || nextExperience < previousExperience
            || totalExperience < previousExperience
            || totalExperience > nextExperience)
            return false;

        snapshot = new Snapshot(path, ReadBaseName(reader, components), level, totalExperience,
            previousExperience, nextExperience);
        return true;
    }

    private static readonly Snapshot Empty = new(string.Empty, string.Empty, 0, 0, 0, 0);

    private static string ReadBaseName(MemoryReader reader, IReadOnlyDictionary<string, nint> components)
    {
        if (!components.TryGetValue("Base", out var component)
            || !reader.TryReadStruct<nint>(component + KnownOffsets.BaseComponent.ItemInfo, out var info)
            || info == 0) return string.Empty;
        return NativeString.Read(reader, info + KnownOffsets.ItemInfo.BaseName);
    }

    private static bool LooksLikeUserAddress(nint address)
        => (long)address is > 0x10000 and < 0x7FFF_FFFF_FFFF;
}
