using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Strict read-only view of the HUD gem-level-up panel. Rows are bound to their exact live gem
/// entity through the row's hidden icon child; row ordering is never used as item identity.
/// </summary>
public sealed class GemLevelUpView
{
    public sealed record Control(nint Element, ElementGeometry.Rect Rect, uint Flags);

    public sealed record Row(
        nint Element,
        nint GemEntity,
        SkillGemReader.Snapshot Gem,
        Control LevelControl,
        Control DismissControl,
        ElementGeometry.Rect Rect);

    private GemLevelUpView(
        nint panel,
        ElementGeometry.Rect panelRect,
        Control? allControl,
        IReadOnlyList<Row> rows)
    {
        Panel = panel;
        PanelRect = panelRect;
        AllControl = allControl;
        Rows = rows;
    }

    public nint Panel { get; }
    public ElementGeometry.Rect PanelRect { get; }
    public Control? AllControl { get; }
    public IReadOnlyList<Row> Rows { get; }
    public bool IsVisible => Panel != 0 && Rows.Count > 0;

    public static GemLevelUpView Read(MemoryReader reader, nint ingameState)
    {
        if (!reader.TryReadStruct<nint>(ingameState + KnownOffsets.IngameState.IngameUi, out var ui)
            || !LooksLikeUserAddress(ui)
            || !reader.TryReadStruct<nint>(ui + KnownOffsets.IngameUiElements.GemLvlUpPanel, out var panel)
            || !LooksLikeUserAddress(panel)
            || !ElementReader.IsVisibleDeep(reader, panel)
            || ElementGeometry.TryReadRect(reader, panel) is not { Width: > 0, Height: > 0 } panelRect)
            return Empty;

        var panelNode = ElementReader.TryReadSnapshot(reader, panel, 4);
        if (panelNode is null || panelNode.Children.Count != 1) return Empty;
        var root = ElementReader.TryReadSnapshot(reader, panelNode.Children[0], 4);
        if (root is null || root.Children.Count != 2) return Empty;

        var all = ReadAllControl(reader, root.Children[0]);
        var rows = ReadRows(reader, root.Children[1]);
        return rows.Count == 0 ? Empty : new GemLevelUpView(panel, panelRect, all, rows);
    }

    private static Control? ReadAllControl(MemoryReader reader, nint aggregate)
    {
        var node = ElementReader.TryReadSnapshot(reader, aggregate, 4);
        if (node is null || node.Children.Count != 2) return null;
        var button = node.Children[0];
        var buttonNode = ElementReader.TryReadSnapshot(reader, button, 2);
        if (buttonNode is null || buttonNode.Children.Count != 1
            || !ElementReader.IsVisibleDeep(reader, button)
            || ElementGeometry.TryReadRect(reader, button) is not { Width: > 0, Height: > 0 } rect)
            return null;
        var label = ReadText(reader, buttonNode.Children[0]);
        if (!label.Equals("all", StringComparison.OrdinalIgnoreCase)) return null;
        reader.TryReadStruct<uint>(button + KnownOffsets.Element.Flags, out var flags);
        return new Control(button, rect, flags);
    }

    private static IReadOnlyList<Row> ReadRows(MemoryReader reader, nint viewport)
    {
        var viewportNode = ElementReader.TryReadSnapshot(reader, viewport, 4);
        if (viewportNode is null || viewportNode.Children.Count != 1) return [];
        var container = ElementReader.TryReadSnapshot(reader, viewportNode.Children[0], 64);
        if (container is null || container.Children.Count is < 1 or > 32) return [];

        var rows = new List<Row>(container.Children.Count);
        var seenGems = new HashSet<nint>();
        foreach (var rowAddress in container.Children)
        {
            var row = ElementReader.TryReadSnapshot(reader, rowAddress, 8);
            if (row is null || row.Children.Count != 4
                || !ElementReader.IsVisibleDeep(reader, rowAddress)
                || ElementGeometry.TryReadRect(reader, rowAddress) is not { Width: > 0, Height: > 0 } rowRect)
                return [];

            var dismissAddress = row.Children[0];
            var levelAddress = row.Children[1];
            var dismiss = ElementReader.TryReadSnapshot(reader, dismissAddress, 4);
            if (dismiss is null || dismiss.Children.Count != 1
                || !TryReadControl(reader, dismissAddress, out var dismissControl)
                || !TryReadControl(reader, levelAddress, out var levelControl))
                return [];

            var hiddenIcon = dismiss.Children[0];
            if (!reader.TryReadStruct<nint>(hiddenIcon + KnownOffsets.GemLevelUpRow.GemEntity, out var gemEntity)
                || !LooksLikeUserAddress(gemEntity)
                || !seenGems.Add(gemEntity)
                || !SkillGemReader.TryRead(reader, gemEntity, out var gem))
                return [];

            rows.Add(new Row(rowAddress, gemEntity, gem, levelControl, dismissControl, rowRect));
        }
        return rows.OrderBy(x => x.Rect.Y).ToArray();
    }

    private static bool TryReadControl(MemoryReader reader, nint address, out Control control)
    {
        control = default!;
        if (!ElementReader.IsVisibleDeep(reader, address)
            || ElementGeometry.TryReadRect(reader, address) is not { Width: > 0, Height: > 0 } rect)
            return false;
        reader.TryReadStruct<uint>(address + KnownOffsets.Element.Flags, out var flags);
        control = new Control(address, rect, flags);
        return true;
    }

    private static string ReadText(MemoryReader reader, nint address)
    {
        var text = NativeString.Read(reader, address + KnownOffsets.Element.TextNoTags);
        return string.IsNullOrWhiteSpace(text)
            ? NativeString.Read(reader, address + KnownOffsets.Element.Text).Trim()
            : text.Trim();
    }

    private static bool LooksLikeUserAddress(nint address)
        => (long)address is > 0x10000 and < 0x7FFF_FFFF_FFFF;

    private static readonly GemLevelUpView Empty = new(0, default, null, []);
}
