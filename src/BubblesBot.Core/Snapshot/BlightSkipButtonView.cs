using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// The "skip" button on the blight encounter UI — the small fast-forward button that ends the
/// wait timer immediately once defenses are set. Lives at
/// <c>IngameUi.LeagueMechanicButtons.Children[2]</c> (mirrors AutoExile's
/// <c>BlightMode.TickFastForward</c>).
///
/// <para>The element only exists / is visible while a blight encounter is active; outside the
/// encounter the parent panel hides itself, so <see cref="IsVisible"/> is the gate. We don't
/// distinguish "panel hidden" from "button missing" — for the bot's purposes they're the same
/// "don't click."</para>
/// </summary>
public sealed class BlightSkipButtonView
{
    private const int SkipButtonChildIndex = 2;
    private const uint VisibleBit = 0x800;
    private const int MaxParentDepth = 32;

    private readonly MemoryReader _reader;
    private readonly nint _buttonAddress;

    public bool IsVisible { get; }
    public ElementGeometry.Rect? ClickRect { get; }

    private BlightSkipButtonView(MemoryReader reader, nint buttonAddress, bool isVisible, ElementGeometry.Rect? rect)
    {
        _reader = reader;
        _buttonAddress = buttonAddress;
        IsVisible = isVisible;
        ClickRect = rect;
    }

    public static BlightSkipButtonView From(MemoryReader reader, nint ingameStateAddress)
    {
        if (!reader.TryReadStruct<nint>(ingameStateAddress + KnownOffsets.IngameState.IngameUi, out var ingameUi) || ingameUi == 0)
            return new BlightSkipButtonView(reader, 0, false, null);
        if (!reader.TryReadStruct<nint>(ingameUi + KnownOffsets.IngameUiElements.LeagueMechanicButtons, out var panel) || panel == 0)
            return new BlightSkipButtonView(reader, 0, false, null);
        if (!reader.TryReadStruct<nint>(panel + KnownOffsets.Element.Childs, out var childsBegin) || childsBegin == 0)
            return new BlightSkipButtonView(reader, 0, false, null);
        if (!reader.TryReadStruct<nint>(childsBegin + SkipButtonChildIndex * 8, out var btn) || btn == 0)
            return new BlightSkipButtonView(reader, 0, false, null);

        var visible = IsElementVisible(reader, btn);
        var rect = visible ? ElementGeometry.TryReadRect(reader, btn) : null;
        return new BlightSkipButtonView(reader, btn, visible, rect);
    }

    private static bool IsElementVisible(MemoryReader reader, nint elementAddress)
        => BubblesBot.Core.Game.ElementReader.IsVisibleDeep(reader, elementAddress);
}
