using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Fail-closed view of the active "End Delirium Encounter" league-mechanic button. The current
/// UI exposes a shared league-button row rather than a typed Delirium pointer. Live capture on
/// the current client established child 1 as the Delirium button, so other simultaneously visible
/// league buttons do not make the target ambiguous.
/// </summary>
public sealed class DeliriumEndButtonView
{
    private const int EndEncounterChildIndex = 1;
    public bool IsVisible { get; }
    public ElementGeometry.Rect? ClickRect { get; }
    public int VisibleLeagueButtons { get; }
    public int ChildIndex { get; }

    private DeliriumEndButtonView(
        bool isVisible, ElementGeometry.Rect? clickRect, int visibleLeagueButtons, int childIndex)
    {
        IsVisible = isVisible;
        ClickRect = clickRect;
        VisibleLeagueButtons = visibleLeagueButtons;
        ChildIndex = childIndex;
    }

    public static DeliriumEndButtonView FromIngameUi(
        MemoryReader reader, nint ingameStateAddress)
    {
        if (!reader.TryReadStruct<nint>(
                ingameStateAddress + KnownOffsets.IngameState.IngameUi, out var ui)
            || ui == 0
            || !reader.TryReadStruct<nint>(
                ui + KnownOffsets.IngameUiElements.LeagueMechanicButtons, out var root)
            || root == 0)
            return Empty();

        var panel = ElementReader.TryReadSnapshot(reader, root, 64);
        if (panel is null) return Empty();

        var visible = new List<(int Index, ElementGeometry.Rect Rect)>();
        for (var i = 0; i < panel.Children.Count; i++)
        {
            var child = panel.Children[i];
            if (!ElementReader.IsVisibleDeep(reader, child)) continue;
            if (ElementGeometry.TryReadRect(reader, child) is not { Width: > 8, Height: > 8 } rect)
                continue;
            visible.Add((i, rect));
        }

        foreach (var item in visible)
            if (item.Index == EndEncounterChildIndex)
                return new DeliriumEndButtonView(true, item.Rect, visible.Count, item.Index);
        return new DeliriumEndButtonView(false, null, visible.Count, -1);
    }

    private static DeliriumEndButtonView Empty() => new(false, null, 0, -1);
}
