using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Resolves the dedicated socket overlay below a visible inventory item. Socket click targets are
/// derived from overlay anchors; callers must fail closed instead of substituting the item center.
/// </summary>
public static class ItemSocketUiView
{
    public sealed record SocketTarget(
        int SocketIndex,
        nint AnchorElement,
        ElementGeometry.Rect AnchorRect,
        float CenterX,
        float CenterY);

    public sealed record Snapshot(
        nint ItemElement,
        nint SocketRootElement,
        ElementGeometry.Rect SocketRootRect,
        IReadOnlyList<SocketTarget> Targets);

    public static bool TryRead(
        MemoryReader reader,
        InventoryView.Item item,
        ItemSocketsReader.Snapshot sockets,
        out Snapshot snapshot)
    {
        snapshot = Empty;
        if (item.Rect is not { } itemRect || sockets.SocketCount is < 1 or > 6)
            return false;

        var itemElement = ElementReader.TryReadSnapshot(reader, item.ElementAddress, 32);
        if (itemElement is null) return false;

        var candidates = new List<Snapshot>();
        foreach (var child in itemElement.Children)
        {
            var root = ElementReader.TryReadSnapshot(reader, child, 16);
            var rootRect = ElementGeometry.TryReadRect(reader, child);
            if (root is null || rootRect is not { } rr || root.Children.Count != sockets.SocketCount
                || !ElementReader.IsVisibleDeep(reader, child)
                || rr.Width is < 8 or > 100 || rr.Height is < 8 or > 400)
                continue;

            var radius = rr.Width / 2f;
            var targets = new List<SocketTarget>(sockets.SocketCount);
            var valid = true;
            for (var index = 0; index < root.Children.Count; index++)
            {
                var anchor = root.Children[index];
                var anchorRect = ElementGeometry.TryReadRect(reader, anchor);
                if (anchorRect is not { } ar || !ElementReader.IsVisibleDeep(reader, anchor))
                {
                    valid = false;
                    break;
                }

                var centerX = ar.X + radius;
                var centerY = ar.Y + radius;
                if (!Contains(itemRect, centerX, centerY))
                {
                    valid = false;
                    break;
                }
                targets.Add(new SocketTarget(index, anchor, ar, centerX, centerY));
            }

            if (valid && targets.Select(x => (MathF.Round(x.CenterX), MathF.Round(x.CenterY))).Distinct().Count() == targets.Count)
                candidates.Add(new Snapshot(item.ElementAddress, child, rr, targets));
        }

        if (candidates.Count != 1) return false;
        snapshot = candidates[0];
        return true;
    }

    private static bool Contains(ElementGeometry.Rect rect, float x, float y)
        => x >= rect.X && x <= rect.X + rect.Width && y >= rect.Y && y <= rect.Y + rect.Height;

    private static readonly Snapshot Empty = new(0, 0, default, []);
}
