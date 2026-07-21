using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Bounded read-only inventory of visible text elements below the in-game UI root. This is a
/// discovery surface for small modal menus whose stable panel pointer is not yet known; promoted
/// interaction code should narrow to a specific panel once its live structure is validated.
/// </summary>
public sealed class VisibleUiTextView
{
    public sealed record TextElement(
        nint Element,
        string TreePath,
        string Text,
        ElementGeometry.Rect? Rect,
        int ChildCount);

    private VisibleUiTextView(nint root, IReadOnlyList<TextElement> elements, int visited)
    {
        Root = root;
        Elements = elements;
        Visited = visited;
    }

    public nint Root { get; }
    public IReadOnlyList<TextElement> Elements { get; }
    public int Visited { get; }

    public IReadOnlyList<TextElement> FindExact(string text)
        => Elements.Where(x => string.Equals(x.Text.Trim(), text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.ChildCount)
            .ThenBy(x => x.Rect?.Width ?? float.MaxValue)
            .ToArray();

    public IReadOnlyList<TextElement> FindContaining(string text)
        => Elements.Where(x => x.Text.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.ChildCount)
            .ThenBy(x => x.Rect?.Width ?? float.MaxValue)
            .ToArray();

    public static VisibleUiTextView ReadInGame(
        MemoryReader reader,
        nint ingameState,
        int maxNodes = 8_192,
        int maxDepth = 20)
    {
        if (!reader.TryReadStruct<nint>(ingameState + KnownOffsets.IngameState.UIRoot, out var root)
            || root == 0)
            return new VisibleUiTextView(0, [], 0);

        var result = new List<TextElement>();
        var queue = new Queue<(nint Address, string Path, int Depth)>();
        var seen = new HashSet<nint>();
        queue.Enqueue((root, "root", 0));
        while (queue.Count > 0 && seen.Count < maxNodes)
        {
            var (address, path, depth) = queue.Dequeue();
            if (!seen.Add(address) || !ElementReader.IsVisibleDeep(reader, address)) continue;
            var snapshot = ElementReader.TryReadSnapshot(reader, address, 512);
            if (snapshot is null) continue;

            var text = ReadElementText(reader, address).Trim();
            if (!string.IsNullOrWhiteSpace(text))
                result.Add(new TextElement(
                    address,
                    path,
                    text,
                    ElementGeometry.TryReadRect(reader, address),
                    snapshot.Children.Count));

            // A hidden ancestor makes all descendants hidden. Traversing only deeply visible
            // branches both preserves semantics and keeps the root scan tightly bounded.
            if (depth >= maxDepth) continue;
            for (var i = 0; i < snapshot.Children.Count; i++)
                queue.Enqueue((snapshot.Children[i], $"{path}/{i}", depth + 1));
        }
        return new VisibleUiTextView(root, result, seen.Count);
    }

    private static string ReadElementText(MemoryReader reader, nint element)
    {
        var text = NativeString.Read(reader, element + KnownOffsets.Element.TextNoTags);
        return string.IsNullOrWhiteSpace(text)
            ? NativeString.Read(reader, element + KnownOffsets.Element.Text)
            : text;
    }
}
