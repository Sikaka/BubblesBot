using System.Globalization;
using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>Current-build Ritual Favours window and its typed item offers.</summary>
public sealed class RitualWindowView
{
    public readonly record struct Offer(
        nint ElementAddress,
        nint ItemEntity,
        ElementGeometry.Rect Rect,
        string MetadataPath,
        string BaseName,
        EntityListReader.EntityRarity Rarity,
        bool Identified,
        int ItemLevel,
        int Quality,
        int GemLevel,
        bool Corrupted,
        int StackSize,
        int InventorySlots,
        string ResourcePath,
        int ClusterPassiveCount);

    public bool IsVisible { get; }
    public int Tribute { get; }
    public int RitualsRemaining { get; }
    public int RerollsRemaining { get; }
    public ElementGeometry.Rect? RerollRect { get; }
    public ElementGeometry.Rect? CloseRect { get; }
    public IReadOnlyList<Offer> Offers { get; }

    private RitualWindowView(
        bool visible, int tribute, int ritualsRemaining, int rerollsRemaining,
        ElementGeometry.Rect? rerollRect, ElementGeometry.Rect? closeRect,
        IReadOnlyList<Offer> offers)
    {
        IsVisible = visible;
        Tribute = tribute;
        RitualsRemaining = ritualsRemaining;
        RerollsRemaining = rerollsRemaining;
        RerollRect = rerollRect;
        CloseRect = closeRect;
        Offers = offers;
    }

    public static RitualWindowView FromIngameUi(MemoryReader reader, nint ingameState)
    {
        if (!reader.TryReadStruct<nint>(ingameState + KnownOffsets.IngameState.IngameUi, out var ui)
            || ui == 0
            || !reader.TryReadStruct<nint>(ui + KnownOffsets.IngameUiElements.RitualWindow, out var panel)
            || panel == 0
            || !ElementReader.IsVisibleDeep(reader, panel))
            return Closed;

        var tribute = ParseFirstInteger(ReadText(reader, panel, 7, 0));
        var rituals = ParseFirstInteger(ReadText(reader, panel, 4, 0));
        var rerolls = ParseFirstInteger(ReadText(reader, panel, 12, 0));
        var rerollRect = ChildRect(reader, panel, 12);
        var closeRect = ChildRect(reader, panel, 9);
        var offers = ReadOffers(reader, panel);
        return new RitualWindowView(true, tribute, rituals, rerolls, rerollRect, closeRect, offers);
    }

    private static readonly RitualWindowView Closed = new(
        false, 0, 0, 0, null, null, Array.Empty<Offer>());

    private static IReadOnlyList<Offer> ReadOffers(MemoryReader reader, nint panel)
    {
        if (!ElementReader.TryGetChild(reader, panel, 11, out var grid))
            return Array.Empty<Offer>();
        var snapshot = ElementReader.TryReadSnapshot(reader, grid, maxChildren: 128);
        if (snapshot is null) return Array.Empty<Offer>();

        var offers = new List<Offer>();
        foreach (var cell in snapshot.Children)
        {
            if (!reader.TryReadStruct<nint>(cell + KnownOffsets.NormalInventoryItem.Item, out var item)
                || item == 0
                || EntityListReader.ReadEntityPath(reader, item) is not { } path
                || !path.StartsWith("Metadata/Items/", StringComparison.Ordinal)
                || ElementGeometry.TryReadRect(reader, cell) is not { } rect)
                continue;

            var components = EntityComponents.ReadComponentMap(reader, item);
            var baseName = "";
            var rarity = EntityListReader.EntityRarity.Normal;
            var identified = false;
            var itemLevel = 0;
            var quality = 0;
            var gemLevel = 0;
            var corrupted = false;
            var stack = 1;
            var width = 1;
            var height = 1;
            var resource = "";

            if (components.TryGetValue("Base", out var baseAddress))
            {
                if (reader.TryReadStruct<nint>(baseAddress + KnownOffsets.BaseComponent.ItemInfo, out var info)
                    && info != 0)
                {
                    baseName = NativeString.Read(reader, info + KnownOffsets.ItemInfo.BaseName);
                    if (reader.TryReadStruct<byte>(info + KnownOffsets.ItemInfo.CellsWidth, out var w)
                        && w is >= 1 and <= 4) width = w;
                    if (reader.TryReadStruct<byte>(info + KnownOffsets.ItemInfo.CellsHeight, out var h)
                        && h is >= 1 and <= 4) height = h;
                }
                if (reader.TryReadStruct<byte>(baseAddress + KnownOffsets.BaseComponent.Corrupted, out var c))
                    corrupted = (c & 1) != 0;
            }
            if (components.TryGetValue("Mods", out var mods))
            {
                if (reader.TryReadStruct<int>(mods + KnownOffsets.ModsComponent.ItemRarity, out var r)
                    && r is >= 0 and <= 4)
                    rarity = (EntityListReader.EntityRarity)r;
                if (reader.TryReadStruct<byte>(mods + KnownOffsets.ModsComponent.Identified, out var id))
                    identified = id != 0;
                reader.TryReadStruct(mods + KnownOffsets.ModsComponent.ItemLevel, out itemLevel);
            }
            if (components.TryGetValue("Quality", out var qualityAddress))
                reader.TryReadStruct(qualityAddress + KnownOffsets.QualityComponent.CurrentQuality, out quality);
            if (components.TryGetValue("SkillGem", out var gemAddress))
                reader.TryReadStruct(gemAddress + KnownOffsets.SkillGemComponent.Level, out gemLevel);
            if (components.TryGetValue("Stack", out var stackAddress)
                && reader.TryReadStruct<int>(stackAddress + KnownOffsets.StackComponent.CurrentCount, out var count)
                && count > 0)
                stack = count;
            if (components.TryGetValue("RenderItem", out var renderAddress)
                && reader.TryReadStruct<nint>(renderAddress + KnownOffsets.RenderItemComponent.ResourcePath, out var resourcePtr)
                && resourcePtr != 0)
                resource = reader.ReadStringUtf16(resourcePtr, 260);

            var passives = 0;
            foreach (var (id, value) in ItemStatsReader.Read(reader, item))
                if (id == 10980 && value is >= 1 and <= 35) { passives = value; break; }

            offers.Add(new Offer(cell, item, rect, path, baseName, rarity, identified,
                itemLevel, quality, gemLevel, corrupted, stack, width * height, resource, passives));
        }
        return offers;
    }

    private static string ReadText(MemoryReader reader, nint root, params int[] path)
    {
        var address = root;
        foreach (var index in path)
            if (!ElementReader.TryGetChild(reader, address, index, out address)) return "";
        var text = NativeString.Read(reader, address + KnownOffsets.Element.TextNoTags);
        return string.IsNullOrWhiteSpace(text)
            ? NativeString.Read(reader, address + KnownOffsets.Element.Text)
            : text;
    }

    private static ElementGeometry.Rect? ChildRect(MemoryReader reader, nint root, params int[] path)
    {
        var address = root;
        foreach (var index in path)
            if (!ElementReader.TryGetChild(reader, address, index, out address)) return null;
        return ElementGeometry.TryReadRect(reader, address);
    }

    private static int ParseFirstInteger(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var digits = new string(text.TakeWhile(c => !char.IsDigit(c))
            .Concat(text.SkipWhile(c => !char.IsDigit(c)).TakeWhile(c => char.IsDigit(c) || c == ','))
            .Where(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value : 0;
    }
}
