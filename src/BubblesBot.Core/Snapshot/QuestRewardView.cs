using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Read-only view of item-backed choices currently rendered inside the quest reward panel.
/// This deliberately has no quest-name or class-specific reward table: identity comes from
/// the live item entity and its rendered tooltip.
/// </summary>
public sealed class QuestRewardView
{
    public sealed record Choice(
        nint Element,
        nint Item,
        nint RenderedTooltip,
        string TreePath,
        string Metadata,
        string BaseName,
        ElementGeometry.Rect? Rect,
        bool IsVisible,
        IReadOnlyList<string> TooltipLines);

    private QuestRewardView(bool isOpen, nint panel, nint hover, IReadOnlyList<Choice> choices)
    {
        IsOpen = isOpen;
        Panel = panel;
        HoverElement = hover;
        Choices = choices;
    }

    public bool IsOpen { get; }
    public nint Panel { get; }
    public nint HoverElement { get; }
    public IReadOnlyList<Choice> Choices { get; }
    public Choice? HoveredChoice { get; private init; }

    public static QuestRewardView Read(MemoryReader reader, nint ingameState)
    {
        reader.TryReadStruct<nint>(ingameState + KnownOffsets.IngameState.UIHover, out var hover);
        if (!reader.TryReadStruct<nint>(ingameState + KnownOffsets.IngameState.IngameUi, out var ingameUi)
            || ingameUi == 0
            || !reader.TryReadStruct<nint>(ingameState + KnownOffsets.IngameState.UIRoot, out var uiRoot)
            || uiRoot == 0)
            return new QuestRewardView(false, 0, hover, []);

        var panel = FindRewardPanel(reader, uiRoot);
        if (panel == 0)
            return new QuestRewardView(false, 0, hover, []);

        var choices = new List<Choice>();
        var queue = new Queue<(nint Address, string Path, int Depth)>();
        var seen = new HashSet<nint>();
        var seenItems = new HashSet<nint>();
        queue.Enqueue((panel, "reward", 0));
        while (queue.Count > 0 && seen.Count < 4_096)
        {
            var (address, path, depth) = queue.Dequeue();
            if (!seen.Add(address)) continue;
            var element = ElementReader.TryReadSnapshot(reader, address, 256);
            if (element is null) continue;

            if (reader.TryReadStruct<nint>(address + KnownOffsets.NormalInventoryItem.Item, out var item)
                && item != 0
                && seenItems.Add(item))
            {
                var metadata = EntityListReader.ReadEntityPath(reader, item);
                if (metadata.StartsWith("Metadata/Items/", StringComparison.Ordinal))
                {
                    var components = EntityComponents.ReadComponentMap(reader, item);
                    reader.TryReadStruct<nint>(address + KnownOffsets.Element.RenderedTooltip, out var tooltip);
                    choices.Add(new Choice(
                        address,
                        item,
                        tooltip,
                        path,
                        metadata,
                        ReadBaseName(reader, components),
                        ElementGeometry.TryReadRect(reader, address),
                        ElementReader.IsVisibleDeep(reader, address),
                        ReadVisibleTextTree(reader, tooltip)));
                }
            }

            if (depth >= 18) continue;
            for (var i = 0; i < element.Children.Count; i++)
                queue.Enqueue((element.Children[i], $"{path}/{i}", depth + 1));
        }

        return new QuestRewardView(true, panel, hover, choices, reader);
    }

    private static nint FindRewardPanel(MemoryReader reader, nint uiRoot)
    {
        var queue = new Queue<(nint Address, int Depth)>();
        var seen = new HashSet<nint>();
        queue.Enqueue((uiRoot, 0));
        while (queue.Count > 0 && seen.Count < 12_000)
        {
            var (address, depth) = queue.Dequeue();
            if (!seen.Add(address) || !ElementReader.IsVisibleDeep(reader, address)) continue;
            var element = ElementReader.TryReadSnapshot(reader, address, 512);
            if (element is null) continue;
            var text = NativeString.Read(reader, address + KnownOffsets.Element.TextNoTags);
            if (string.IsNullOrWhiteSpace(text))
                text = NativeString.Read(reader, address + KnownOffsets.Element.Text);
            if (string.Equals(text.Trim(), "Select One Reward", StringComparison.OrdinalIgnoreCase))
            {
                var current = address;
                // Validated layout: panel/0/0 is the unique title. Resolve the panel from
                // the title rather than pinning the session-specific UIRoot child index.
                for (var parentDepth = 0; parentDepth < 2 && current != 0; parentDepth++)
                {
                    if (!reader.TryReadStruct<nint>(current + KnownOffsets.Element.Parent, out var parent)
                        || parent == 0 || parent == current)
                        break;
                    current = parent;
                }
                if (current != 0 && current != address && ElementReader.IsVisibleDeep(reader, current))
                    return current;
            }
            if (depth >= 24) continue;
            foreach (var child in element.Children) queue.Enqueue((child, depth + 1));
        }
        return 0;
    }

    private QuestRewardView(
        bool isOpen,
        nint panel,
        nint hover,
        IReadOnlyList<Choice> choices,
        MemoryReader reader)
        : this(isOpen, panel, hover, choices)
    {
        HoveredChoice = ResolveHoveredChoice(reader, choices, hover);
    }

    private static Choice? ResolveHoveredChoice(MemoryReader reader, IReadOnlyList<Choice> choices, nint hover)
    {
        if (hover == 0) return null;
        var choiceElements = choices.ToDictionary(x => x.Element);
        var current = hover;
        for (var depth = 0; depth < 24 && current != 0; depth++)
        {
            if (choiceElements.TryGetValue(current, out var choice)) return choice;
            if (!reader.TryReadStruct<nint>(current + KnownOffsets.Element.Parent, out var parent)
                || parent == current)
                break;
            current = parent;
        }
        return null;
    }

    private static string ReadBaseName(MemoryReader reader, IReadOnlyDictionary<string, nint> components)
    {
        if (!components.TryGetValue("Base", out var component)
            || !reader.TryReadStruct<nint>(component + KnownOffsets.BaseComponent.ItemInfo, out var info)
            || info == 0)
            return string.Empty;
        return NativeString.Read(reader, info + KnownOffsets.ItemInfo.BaseName);
    }

    private static IReadOnlyList<string> ReadVisibleTextTree(MemoryReader reader, nint root)
    {
        if (root == 0) return [];
        var lines = new List<string>();
        var queue = new Queue<(nint Address, int Depth)>();
        var seen = new HashSet<nint>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0 && seen.Count < 1_024)
        {
            var (address, depth) = queue.Dequeue();
            if (!seen.Add(address)) continue;
            var element = ElementReader.TryReadSnapshot(reader, address, 128);
            if (element is null) continue;
            if (ElementReader.IsVisibleDeep(reader, address))
            {
                var text = NativeString.Read(reader, address + KnownOffsets.Element.TextNoTags);
                if (string.IsNullOrWhiteSpace(text))
                    text = NativeString.Read(reader, address + KnownOffsets.Element.Text);
                foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (!string.IsNullOrWhiteSpace(line)) lines.Add(line);
            }
            if (depth >= 14) continue;
            foreach (var child in element.Children) queue.Enqueue((child, depth + 1));
        }
        return lines;
    }
}
