using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>Bounded inventory of deeply-visible elements below the in-game UIRoot.</summary>
public sealed class VisibleUiStructureView
{
    public sealed record Node(
        nint Element,
        nint Parent,
        string TreePath,
        int Depth,
        int ChildCount,
        uint Flags,
        ElementGeometry.Rect? Rect);

    private VisibleUiStructureView(nint root, IReadOnlyList<Node> nodes)
    {
        Root = root;
        Nodes = nodes;
    }

    public nint Root { get; }
    public IReadOnlyList<Node> Nodes { get; }

    public static VisibleUiStructureView ReadInGame(
        MemoryReader reader,
        nint ingameState,
        int maxNodes = 20_000,
        int maxDepth = 32)
    {
        if (!reader.TryReadStruct<nint>(ingameState + KnownOffsets.IngameState.UIRoot, out var root)
            || root == 0)
            return new VisibleUiStructureView(0, []);

        var nodes = new List<Node>();
        var queue = new Queue<(nint Address, string Path, int Depth)>();
        var seen = new HashSet<nint>();
        queue.Enqueue((root, "root", 0));
        while (queue.Count > 0 && seen.Count < maxNodes)
        {
            var (address, path, depth) = queue.Dequeue();
            if (!seen.Add(address) || !ElementReader.IsVisibleDeep(reader, address)) continue;
            var snapshot = ElementReader.TryReadSnapshot(reader, address, 512);
            if (snapshot is null) continue;
            reader.TryReadStruct<uint>(address + KnownOffsets.Element.Flags, out var flags);
            nodes.Add(new Node(
                address,
                snapshot.Parent,
                path,
                depth,
                snapshot.Children.Count,
                flags,
                ElementGeometry.TryReadRect(reader, address)));
            if (depth >= maxDepth) continue;
            for (var i = 0; i < snapshot.Children.Count; i++)
                queue.Enqueue((snapshot.Children[i], $"{path}/{i}", depth + 1));
        }
        return new VisibleUiStructureView(root, nodes);
    }
}
