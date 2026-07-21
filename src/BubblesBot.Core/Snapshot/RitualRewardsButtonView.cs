using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>The global right-side Ritual Favours button and its completed/total counter.</summary>
public sealed class RitualRewardsButtonView
{
    public bool IsVisible { get; }
    public ElementGeometry.Rect? ClickRect { get; }
    public int Completed { get; }
    public int Total { get; }

    private RitualRewardsButtonView(
        bool isVisible, ElementGeometry.Rect? clickRect, int completed, int total)
    {
        IsVisible = isVisible;
        ClickRect = clickRect;
        Completed = completed;
        Total = total;
    }

    public static RitualRewardsButtonView FromIngameUi(
        MemoryReader reader, nint ingameStateAddress)
    {
        if (!reader.TryReadStruct<nint>(
                ingameStateAddress + KnownOffsets.IngameState.UIRoot, out var uiRoot)
            || uiRoot == 0)
            return Empty();

        var button = UiTreeNavigator.Resolve(reader, uiRoot, UiIndexPaths.RitualRewardsButton);
        if (button == 0 || !ElementReader.IsVisibleDeep(reader, button)) return Empty();

        // Current tree: button[0][0] contains "completed/total" (for example 4/4).
        var textElement = UiTreeNavigator.ChildAt(reader, button, 0);
        textElement = UiTreeNavigator.ChildAt(reader, textElement, 0);
        var text = textElement == 0
            ? string.Empty
            : NativeString.Read(reader, textElement + KnownOffsets.Element.TextNoTags);
        if (string.IsNullOrWhiteSpace(text) && textElement != 0)
            text = NativeString.Read(reader, textElement + KnownOffsets.Element.Text);

        var completed = 0;
        var total = 0;
        var slash = text.IndexOf('/');
        if (slash <= 0
            || !int.TryParse(text.AsSpan(0, slash).Trim(), out completed)
            || !int.TryParse(text.AsSpan(slash + 1).Trim(), out total)
            || total <= 0)
            return Empty(); // fail closed if this committed path now points at another UI element

        var rect = ElementGeometry.TryReadRect(reader, button);
        var visible = rect is { Width: > 1, Height: > 1 };
        return new RitualRewardsButtonView(visible, visible ? rect : null, completed, total);
    }

    private static RitualRewardsButtonView Empty() => new(false, null, 0, 0);
}
