namespace BubblesBot.Core.Game;

public static class GroundLabelReader
{
    public sealed record GroundLabelSnapshot(
        nint Address,
        nint LabelElement,
        nint ItemEntity);

    public static IReadOnlyList<GroundLabelSnapshot> ReadLabels(MemoryReader reader, nint labelsRoot, int maxLabels = 1000)
    {
        var result = new List<GroundLabelSnapshot>();
        if (!LooksLikeUserAddress(labelsRoot))
            return result;

        if (!reader.TryReadStruct<nint>(labelsRoot + KnownOffsets.ItemsOnGroundLabelElement.LabelsListSentinel, out var sentinel)
            || !LooksLikeUserAddress(sentinel))
            return result;

        if (!reader.TryReadStruct<nint>(sentinel, out var current))
            return result;

        var visited = new HashSet<nint>();
        while (current != sentinel && LooksLikeUserAddress(current) && visited.Add(current))
        {
            if (result.Count >= maxLabels)
                break;

            if (reader.TryReadStruct<nint>(current + KnownOffsets.LabelOnGround.ItemOnGround, out var item)
                && reader.TryReadStruct<nint>(current + KnownOffsets.LabelOnGround.Label, out var label)
                && LooksLikeUserAddress(item)
                && LooksLikeUserAddress(label))
            {
                result.Add(new GroundLabelSnapshot(current, label, item));
            }

            if (!reader.TryReadStruct<nint>(current, out current))
                break;
        }

        return result;
    }

    private static bool LooksLikeUserAddress(nint p)
    {
        var v = (long)p;
        return v > 0x10000 && v < 0x7FFF_FFFF_FFFF;
    }
}
