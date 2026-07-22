namespace BubblesBot.Core.Game;

/// <summary>Game-authored transition categories exposed by the AreaTransition component.</summary>
public enum AreaTransitionType : byte
{
    Normal = 0,
    Local = 1,
    NormalToCorrupted = 2,
    CorruptedToNormal = 3,
    Labyrinth = 5,
}

/// <summary>Frozen identity of one area-transition entity.</summary>
public readonly record struct AreaTransitionIdentity(
    ushort AreaId,
    AreaTransitionType Type,
    string DestinationAreaId,
    string DestinationAreaName);

/// <summary>
/// Reads the semantic transition identity that is not present in the entity metadata path.
/// In particular, Vaal side-area entrances use the same
/// <c>Metadata/MiscellaneousObjects/AreaTransition</c> path as ordinary doors, but are
/// explicitly typed <see cref="AreaTransitionType.NormalToCorrupted"/> by the game.
/// </summary>
public static class AreaTransitionIdentityReader
{
    public static bool TryRead(
        MemoryReader reader,
        nint component,
        out AreaTransitionIdentity identity)
    {
        identity = default;
        if (!LooksLikeUserAddress(component)
            || !reader.TryReadStruct<ushort>(
                component + KnownOffsets.AreaTransitionComponent.AreaId, out var areaId)
            || !reader.TryReadStruct<byte>(
                component + KnownOffsets.AreaTransitionComponent.TransitionType, out var typeRaw)
            || !Enum.IsDefined(typeof(AreaTransitionType), typeRaw))
            return false;

        var destinationId = string.Empty;
        var destinationName = string.Empty;
        if (reader.TryReadStruct<nint>(
                component + KnownOffsets.AreaTransitionComponent.WorldAreaInfoPtr, out var worldArea)
            && LooksLikeUserAddress(worldArea))
        {
            destinationId = ReadUtf16Pointer(reader, worldArea + KnownOffsets.WorldArea.Id, 64);
            destinationName = ReadUtf16Pointer(reader, worldArea + KnownOffsets.WorldArea.Name, 128);
        }

        identity = new AreaTransitionIdentity(
            areaId, (AreaTransitionType)typeRaw, destinationId, destinationName);
        return true;
    }

    private static string ReadUtf16Pointer(MemoryReader reader, nint address, int maxChars)
    {
        try
        {
            if (reader.TryReadStruct<nint>(address, out var pointer)
                && LooksLikeUserAddress(pointer))
                return reader.ReadStringUtf16(pointer, maxChars).TrimEnd('\0');
        }
        catch
        {
            // A streamed transition may expose its component before WorldArea is ready.
        }
        return string.Empty;
    }

    private static bool LooksLikeUserAddress(nint address)
    {
        var value = (long)address;
        return value > 0x10000 && value < 0x7FFF_FFFF_FFFF;
    }
}
