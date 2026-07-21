using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>Read-only visible text/control surface for the normal NPC dialog panel.</summary>
public sealed class NpcDialogView
{
    public sealed record TextControl(
        nint Element,
        string TreePath,
        string Text,
        ElementGeometry.Rect? Rect,
        int ChildCount);

    private NpcDialogView(bool isOpen, nint panel, IReadOnlyList<TextControl> controls)
    {
        IsOpen = isOpen;
        Panel = panel;
        Controls = controls;
    }

    public bool IsOpen { get; }
    public nint Panel { get; }
    public IReadOnlyList<TextControl> Controls { get; }

    public IReadOnlyList<TextControl> FindExact(string text)
        => Controls.Where(x => string.Equals(x.Text.Trim(), text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.ChildCount)
            .ThenBy(x => x.Rect?.Width ?? float.MaxValue)
            .ToArray();

    public static NpcDialogView Read(MemoryReader reader, nint ingameState)
    {
        nint panel = 0;
        if (!reader.TryReadStruct<nint>(ingameState + KnownOffsets.IngameState.IngameUi, out var ingameUi)
            || ingameUi == 0
            || !reader.TryReadStruct<nint>(ingameUi + KnownOffsets.IngameUiElements.NpcDialog, out panel)
            || panel == 0
            || !ElementReader.IsVisibleDeep(reader, panel))
            return new NpcDialogView(false, panel, []);

        var controls = new List<TextControl>();
        var queue = new Queue<(nint Address, string Path, int Depth)>();
        var seen = new HashSet<nint>();
        queue.Enqueue((panel, "npc", 0));
        while (queue.Count > 0 && seen.Count < 2_048)
        {
            var (address, path, depth) = queue.Dequeue();
            if (!seen.Add(address)) continue;
            var element = ElementReader.TryReadSnapshot(reader, address, 256);
            if (element is null) continue;
            if (ElementReader.IsVisibleDeep(reader, address))
            {
                var text = ReadElementText(reader, address).Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    controls.Add(new TextControl(
                        address,
                        path,
                        text,
                        ElementGeometry.TryReadRect(reader, address),
                        element.Children.Count));
            }
            if (depth >= 16) continue;
            for (var i = 0; i < element.Children.Count; i++)
                queue.Enqueue((element.Children[i], $"{path}/{i}", depth + 1));
        }
        return new NpcDialogView(true, panel, controls);
    }

    private static string ReadElementText(MemoryReader reader, nint element)
    {
        var text = NativeString.Read(reader, element + KnownOffsets.Element.TextNoTags);
        return string.IsNullOrWhiteSpace(text)
            ? NativeString.Read(reader, element + KnownOffsets.Element.Text)
            : text;
    }
}
