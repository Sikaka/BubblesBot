using System.Globalization;
using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Location-independent identity evidence for an item entity. Entity/element addresses and
/// container coordinates are intentionally excluded because item entities rematerialize when
/// moved between containers. Equal fingerprints form an ambiguity set; callers must use
/// container deltas to prove which member moved.
/// </summary>
public sealed record ItemIdentityFingerprint(
    string Canonical,
    string MetadataPath,
    string BaseName,
    int StackSize,
    int Width,
    int Height,
    bool HasSocketComponent,
    bool SocketDetailsValidated)
{
    public static ItemIdentityFingerprint Capture(MemoryReader reader, InventoryView.Item item)
    {
        var components = EntityComponents.ReadComponentMap(reader, item.ItemEntity);
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["base"] = ReadBaseName(reader, components),
            ["dimensions"] = $"{item.Width}x{item.Height}",
            ["metadata"] = item.Path,
            ["stack"] = item.StackSize.ToString(CultureInfo.InvariantCulture),
            ["stats"] = string.Join(',', ItemStatsReader.Read(reader, item.ItemEntity)
                .OrderBy(x => x.Id).Select(x => $"{x.Id}:{x.Value}")),
        };

        if (components.TryGetValue("Mods", out var mods))
        {
            ReadByte(reader, mods + KnownOffsets.ModsComponent.Identified, "identified", fields);
            Read(reader, mods + KnownOffsets.ModsComponent.ItemRarity, "rarity", fields);
            Read(reader, mods + KnownOffsets.ModsComponent.ItemLevel, "itemLevel", fields);
            // IsMirrored +0x371 is intentionally excluded: A-06 live research on 2026-07-17
            // read 45 for the inventory entity and 0 for the rematerialized equipped entity.
            // That offset is stale until a dedicated oracle validation replaces it.
        }
        // RequiredLevel, Base influence/corruption/scourged flags, and Quality remain excluded
        // until their own current-build oracle gates pass. Socket layout and socketed-gem
        // level/XP are added below only when the complete strict socket read succeeds.
        // A plausible value in one setup is not sufficient to promote it into durable identity.
        if (components.TryGetValue("RenderItem", out var render)
            && reader.TryReadStruct<nint>(render + KnownOffsets.RenderItemComponent.ResourcePath, out var resource)
            && resource != 0)
            fields["resource"] = reader.ReadStringUtf16(resource, 260);

        var hasSockets = components.ContainsKey("Sockets");
        var socketDetailsValidated = !hasSockets;
        if (hasSockets && ItemSocketsReader.TryRead(reader, item.ItemEntity, out var sockets))
        {
            fields["sockets"] = sockets.Canonical;
            socketDetailsValidated = true;
        }
        else
        {
            fields["socketComponent"] = hasSockets ? "present-details-unreadable" : "absent";
        }

        return new ItemIdentityFingerprint(
            string.Join('|', fields.Select(x => $"{x.Key}={Escape(x.Value)}")),
            item.Path,
            fields["base"],
            item.StackSize,
            item.Width,
            item.Height,
            hasSockets,
            SocketDetailsValidated: socketDetailsValidated);
    }

    private static string ReadBaseName(MemoryReader reader, IReadOnlyDictionary<string, nint> components)
    {
        if (!components.TryGetValue("Base", out var component)
            || !reader.TryReadStruct<nint>(component + KnownOffsets.BaseComponent.ItemInfo, out var info)
            || info == 0)
            return string.Empty;
        return NativeString.Read(reader, info + KnownOffsets.ItemInfo.BaseName);
    }

    private static void Read(MemoryReader reader, nint address, string name, IDictionary<string, string> fields)
    {
        if (reader.TryReadStruct<int>(address, out var value))
            fields[name] = value.ToString(CultureInfo.InvariantCulture);
    }

    private static void ReadByte(MemoryReader reader, nint address, string name, IDictionary<string, string> fields)
    {
        if (reader.TryReadStruct<byte>(address, out var value))
            fields[name] = value.ToString(CultureInfo.InvariantCulture);
    }

    private static string Escape(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("=", "\\=", StringComparison.Ordinal);
}
