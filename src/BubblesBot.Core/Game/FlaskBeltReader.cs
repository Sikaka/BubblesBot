namespace BubblesBot.Core.Game;

/// <summary>What kind of flask sits in a belt slot — drives which auto-trigger applies.</summary>
public enum FlaskKind { Empty, Life, Mana, Hybrid, Utility }

/// <summary>
/// Reads the player's flask belt (the 5×1 server inventory) and classifies each slot's flask by its
/// metadata path. Lets the flask automation follow the <b>actual</b> flasks in the belt (life / mana
/// / hybrid / utility) rather than a static slot→trigger guess. Belt contents change rarely, so
/// callers should read this occasionally (e.g. a few times a second), not every frame.
/// </summary>
public static class FlaskBeltReader
{
    /// <summary>ServerInventory.InventType value for the flask belt (ExileApi InventoryTypeE.Flask).
    /// Validated 2026-07-17 via POEMCP: the belt is a 5×2 inventory, type=10, flasks on row 0.</summary>
    private const int FlaskInventoryType = 10;

    public sealed record Slot(int Index, FlaskKind Kind, string Path, int Charges, int ChargesPerUse, int MaxCharges)
    {
        /// <summary>True when the flask has enough charges to actually fire (pressing it otherwise
        /// does nothing). Empty slots are never usable.</summary>
        public bool CanUse => Kind != FlaskKind.Empty && ChargesPerUse > 0 && Charges >= ChargesPerUse;
    }

    /// <summary>Read all 5 belt slots. Missing/unreadable slots come back <see cref="FlaskKind.Empty"/>.</summary>
    public static IReadOnlyList<Slot> Read(MemoryReader reader, nint ingameDataAddress)
    {
        var slots = new Slot[5];
        for (var i = 0; i < 5; i++) slots[i] = new Slot(i, FlaskKind.Empty, string.Empty, 0, 0, 0);

        if (!reader.TryReadStruct<nint>(ingameDataAddress + KnownOffsets.IngameData.ServerData, out var serverData) || serverData == 0)
            return slots;
        if (!reader.TryReadStruct<StdVector>(serverData + KnownOffsets.ServerData.PlayerInventories, out var vector)
            || vector.First == 0 || vector.ByteCount <= 0
            || vector.ByteCount % KnownOffsets.InventoryHolder.Size != 0)
            return slots;

        var count = vector.ByteCount / KnownOffsets.InventoryHolder.Size;
        if (count is < 1 or > 128) return slots;

        for (var h = 0; h < count; h++)
        {
            var holder = vector.First + h * KnownOffsets.InventoryHolder.Size;
            if (!reader.TryReadStruct<nint>(holder + KnownOffsets.InventoryHolder.InventoryPtr, out var inv) || inv == 0)
                continue;
            if (!reader.TryReadStruct<int>(inv + KnownOffsets.ServerInventory.InventType, out var invType)
                || invType != FlaskInventoryType)
                continue;

            foreach (var item in ServerInventoryItemsReader.Read(reader, inv))
            {
                if (item.MinX is < 0 or > 4) continue;
                var path = EntityListReader.ReadEntityPath(reader, item.EntityAddress) ?? string.Empty;
                var (cur, perUse, max) = ReadCharges(reader, item.EntityAddress);
                slots[item.MinX] = new Slot(item.MinX, Classify(path), path, cur, perUse, max);
            }
            break; // found the belt
        }
        return slots;
    }

    /// <summary>Read a flask's (current, per-use, max) charges from its Charges component.</summary>
    private static (int Current, int PerUse, int Max) ReadCharges(MemoryReader reader, nint entity)
    {
        var components = EntityComponents.ReadComponentMap(reader, entity);
        if (!components.TryGetValue("Charges", out var charges) || charges == 0)
            return (0, 0, 0);

        reader.TryReadStruct<int>(charges + KnownOffsets.ChargesComponent.Current, out var current);
        int perUse = 0, max = 0;
        if (reader.TryReadStruct<nint>(charges + KnownOffsets.ChargesComponent.BasePtr, out var basePtr) && basePtr != 0)
        {
            reader.TryReadStruct<int>(basePtr + KnownOffsets.ChargesBase.Max, out max);
            reader.TryReadStruct<int>(basePtr + KnownOffsets.ChargesBase.PerUse, out perUse);
        }
        return (current, perUse, max);
    }

    private static FlaskKind Classify(string path)
    {
        if (string.IsNullOrEmpty(path)) return FlaskKind.Empty;
        // Flask metadata paths look like Metadata/Items/Flasks/FlaskLife1, FlaskMana3, FlaskHybrid2,
        // or utility bases (Quicksilver, Granite, Quartz, Jade, Bismuth, Silver, Basalt, …).
        if (path.Contains("Hybrid", StringComparison.OrdinalIgnoreCase)) return FlaskKind.Hybrid;
        if (path.Contains("Life", StringComparison.OrdinalIgnoreCase)) return FlaskKind.Life;
        if (path.Contains("Mana", StringComparison.OrdinalIgnoreCase)) return FlaskKind.Mana;
        if (path.Contains("Flask", StringComparison.OrdinalIgnoreCase)) return FlaskKind.Utility;
        return FlaskKind.Empty;
    }
}
