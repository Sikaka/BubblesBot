using BubblesBot.Core;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;
using BubblesBot.Research.Probing;

namespace BubblesBot.Research.Probes.Ui;

public sealed class AtlasDeviceProbe : IProbe
{
    public string Name => "ui.atlas-device";
    public string Group => "ui";
    public string Description => "Live atlas map-device subtree, slot contents, and button geometry.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx)
    {
        var atlas = AtlasPanelView.FromIngameUi(ctx.Reader, ctx.Chain.IngameState);
        if (!atlas.IsVisible) return ProbeResult.Skip("atlas is closed");

        var rows = new List<string>
        {
            $"panel=0x{atlas.PanelAddress:X}",
            $"deviceVisible={atlas.IsDevicePanelVisible()}",
            $"activateRect={Format(atlas.ActivateButtonRect())}",
            $"activateReady={atlas.IsActivateReady()}",
        };
        for (var i = 0; i < 6; i++)
        {
            var slot = atlas.DeviceSlot(i);
            rows.Add(slot is null
                ? $"slot[{i}]=missing"
                : $"slot[{i}] rect={Format(slot.Value.Rect)} occupied={slot.Value.IsOccupied}");
        }
        var atlasItems = atlas.StoredItems().Select(item =>
            $"[{item.Index}] {item.Path.Split('/').LastOrDefault()} rect={Format(item.Rect)}");
        rows.Add("atlasItems=" + string.Join(", ", atlasItems));

        var window = Resolve(ctx.Reader, atlas.PanelAddress, 7, 0);
        if (window != 0)
        {
            var queue = new Queue<(nint Address, string Path, int Depth)>();
            queue.Enqueue((window, "[7,0]", 0));
            while (queue.Count > 0)
            {
                var (address, path, depth) = queue.Dequeue();
                var snap = ElementReader.TryReadSnapshot(ctx.Reader, address, 100);
                if (snap is null) continue;
                rows.Add(Describe(ctx.Reader, address, path, snap));
                if (depth >= 2) continue;
                for (var i = 0; i < snap.Children.Count; i++)
                    queue.Enqueue((snap.Children[i], $"{path}[{i}]", depth + 1));
            }
        }

        return ProbeResult.Pass(string.Join(" | ", rows));
    }

    public ProbeResult Discover(ProbeContext ctx)
        => ProbeResult.Found("open atlas device subtree", []);

    private static string Describe(
        MemoryReader reader, nint address, string path, ElementReader.ElementSnapshot snap)
    {
        var rect = ElementGeometry.TryReadRect(reader, address);
        var text = NativeString.Read(reader, address + KnownOffsets.Element.Text)
            .Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ');
        if (text.Length > 40) text = text[..40];
        var itemPath = ReadItemPath(reader, address);
        if (itemPath.Length > 60) itemPath = "..." + itemPath[^57..];
        return $"{path} addr=0x{address:X} children={snap.Children.Count} "
            + $"localVisible={snap.IsVisibleLocal} deepVisible={ElementReader.IsVisibleDeep(reader, address)} "
            + $"rect={Format(rect)} text='{text}' item='{itemPath}'";
    }

    private static string ReadItemPath(MemoryReader reader, nint address)
    {
        if (!reader.TryReadStruct<nint>(
                address + KnownOffsets.NormalInventoryItem.Item, out var entity)
            || entity == 0)
            return string.Empty;
        return EntityListReader.ReadEntityPath(reader, entity) ?? string.Empty;
    }

    private static nint Resolve(MemoryReader reader, nint root, params int[] path)
    {
        var current = root;
        foreach (var index in path)
        {
            if (!ElementReader.TryGetChild(reader, current, index, out current))
                return 0;
        }
        return current;
    }

    private static string Format(ElementGeometry.Rect? rect)
        => rect is { } value
            ? $"{value.X:F0},{value.Y:F0},{value.Width:F0},{value.Height:F0}"
            : "?";
}
