using System.Collections.Concurrent;
using System.Text;
using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Resolves the session-local ids used by ServerData.SkillBarIds to names from the player's
/// live ActorSkill records. These ids are not a stable game-data key, so a static catalog can
/// silently attach the wrong name after a character or client restart.
/// </summary>
internal static class ActorSkillNameReader
{
    private const int MaximumActorSkills = 512;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(10);
    private static readonly ConcurrentDictionary<(int ProcessId, nint Actor), CacheEntry> Cache = new();

    private sealed record CacheEntry(
        DateTimeOffset RefreshedAt,
        IReadOnlyDictionary<ushort, string> Names);

    public static IReadOnlyDictionary<ushort, string> Read(
        GameSnapshot snapshot,
        nint actorComponent,
        IReadOnlyCollection<ushort> requestedIds)
    {
        if (!LooksLikeUserAddress(actorComponent)) return PseudoSkillNames;

        var key = (snapshot.Reader.Process.ProcessId, actorComponent);
        if (Cache.TryGetValue(key, out var cached)
            && DateTimeOffset.UtcNow - cached.RefreshedAt < CacheLifetime
            && requestedIds.All(id => cached.Names.ContainsKey(id)))
            return cached.Names;

        var internalNames = ReadInternalNames(snapshot.Reader, actorComponent);
        var equippedNames = ReadEquippedActiveGemNames(snapshot);
        var displayNames = new Dictionary<ushort, string>(PseudoSkillNames);
        foreach (var (id, internalName) in internalNames)
            displayNames[id] = SkillNameFormatter.ToDisplayName(internalName, equippedNames);

        var result = new CacheEntry(DateTimeOffset.UtcNow, displayNames);
        Cache[key] = result;
        return result.Names;
    }

    private static IReadOnlyDictionary<ushort, string> ReadInternalNames(
        MemoryReader reader,
        nint actorComponent)
    {
        var result = new Dictionary<ushort, string>();
        if (!reader.TryReadStruct<nint>(
                actorComponent + KnownOffsets.ActorComponent.ActorSkillsArray, out var begin)
            || !reader.TryReadStruct<nint>(
                actorComponent + KnownOffsets.ActorComponent.ActorSkillsArray + nint.Size, out var end)
            || !LooksLikeUserAddress(begin)
            || end < begin)
            return result;

        var byteCount = (long)end - (long)begin;
        if (byteCount % nint.Size != 0) return result;
        var count = byteCount / nint.Size;
        if (count is < 1 or > MaximumActorSkills) return result;

        for (var index = 0; index < count; index++)
        {
            if (!reader.TryReadStruct<nint>(begin + index * nint.Size, out var actorSkill)
                || !LooksLikeUserAddress(actorSkill)
                || !reader.TryReadStruct<ushort>(actorSkill + KnownOffsets.ActorSkill.Id, out var id)
                || id == 0
                || !reader.TryReadStruct<nint>(actorSkill + KnownOffsets.ActorSkill.EffectsPerLevel, out var effects)
                || !LooksLikeUserAddress(effects)
                || !reader.TryReadStruct<nint>(effects + KnownOffsets.GrantedEffectsPerLevel.SkillGemWrapper, out var wrapper)
                || !LooksLikeUserAddress(wrapper)
                || !reader.TryReadStruct<nint>(wrapper + KnownOffsets.SkillGemWrapper.InternalName, out var namePointer)
                || !LooksLikeUserAddress(namePointer))
                continue;

            var internalName = reader.ReadStringUtf16(namePointer, 96);
            if (!IsPlausibleInternalName(internalName)) continue;
            result.TryAdd(id, internalName);
        }

        return result;
    }

    private static IReadOnlyCollection<string> ReadEquippedActiveGemNames(GameSnapshot snapshot)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var inventory in EquipmentInventoriesView.From(snapshot).ServerInventories)
            {
                // Body armour through boots. Main inventory is deliberately excluded so spare
                // gems or socketed swap gear cannot rename a skill that is currently equipped.
                if (inventory.InventoryType is < 1 or > 8) continue;
                foreach (var item in ServerInventoryItemsReader.Read(snapshot.Reader, inventory.Address))
                {
                    if (!ItemSocketsReader.TryRead(snapshot.Reader, item.EntityAddress, out var sockets)) continue;
                    foreach (var gem in sockets.SocketedGems)
                    {
                        if (string.IsNullOrWhiteSpace(gem.BaseName)
                            || gem.MetadataPath.Contains("/SupportGem", StringComparison.OrdinalIgnoreCase))
                            continue;
                        names.Add(gem.BaseName.Trim());
                    }
                }
            }
        }
        catch
        {
            // Friendly gem names are presentation data. A torn inventory read must never make
            // the snapshot or combat loop fail; the internal ActorSkill name remains usable.
        }
        return names;
    }

    private static bool IsPlausibleInternalName(string value)
        => value.Length is >= 2 and <= 96
           && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-');

    private static bool LooksLikeUserAddress(nint address)
        => (long)address is > 0x10000 and < 0x7FFF_FFFF_FFFF;

    private static readonly IReadOnlyDictionary<ushort, string> PseudoSkillNames =
        new Dictionary<ushort, string>
        {
            [10505] = "Move only",
        };
}

/// <summary>Turns PoE's internal ActorSkill key into a user-facing label.</summary>
public static class SkillNameFormatter
{
    public static string ToDisplayName(
        string internalName,
        IReadOnlyCollection<string>? equippedGemNames = null)
    {
        if (string.IsNullOrWhiteSpace(internalName)) return string.Empty;
        if (internalName.Equals("Move", StringComparison.OrdinalIgnoreCase)) return "Move only";
        if (internalName.Equals("PlayerMelee", StringComparison.OrdinalIgnoreCase)) return "Basic attack";

        if (equippedGemNames is { Count: > 0 })
        {
            var normalized = Normalize(internalName);
            var exact = equippedGemNames.FirstOrDefault(name => Normalize(name) == normalized);
            if (!string.IsNullOrWhiteSpace(exact)) return exact;

            var root = RemoveAlternateSuffix(normalized);
            var rooted = equippedGemNames
                .Where(name => Normalize(name).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .ToArray();
            if (rooted.Length == 1) return rooted[0];
        }

        return Humanize(internalName);
    }

    private static string Humanize(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var ch = value[index];
            if (ch is '_' or '-')
            {
                if (builder.Length > 0 && builder[builder.Length - 1] != ' ') builder.Append(' ');
                continue;
            }

            if (index > 0 && char.IsUpper(ch)
                && (char.IsLower(value[index - 1])
                    || index + 1 < value.Length && char.IsLower(value[index + 1])))
                builder.Append(' ');
            builder.Append(ch);
        }
        return builder.ToString().Trim();
    }

    private static string Normalize(string value)
        => new(value.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string RemoveAlternateSuffix(string value)
    {
        if (value.Length > 4
            && value[^4..^1].Equals("alt", StringComparison.OrdinalIgnoreCase)
            && char.IsLetter(value[^1]))
            return value[..^4];
        return value;
    }
}
