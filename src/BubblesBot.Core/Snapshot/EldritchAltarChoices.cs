using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>One clickable Eldritch altar option: its button rect (window coords) and the
/// full trade-off text ("Player gains: ..." / "Eldritch Minions gain: ...", newline-separated).</summary>
public sealed record EldritchAltarChoice(ElementGeometry.Rect ClickRect, string Text);

/// <summary>Both options of one altar, in on-screen order (Top = smaller Y).</summary>
public sealed record EldritchAltarChoiceSet(EldritchAltarChoice Top, EldritchAltarChoice Bottom);

/// <summary>
/// Reads the two-choice UI tree hanging off an Eldritch altar's ground label.
///
/// <para>Shape contract (captured live 2026-07-15 via <c>mechanic.eldritch-altar-ui</c>):
/// the label root has exactly 2 children, one per option. Each option element carries the
/// clickable button rect itself and, among its direct children, a text element whose
/// string starts with a "&lt;subject&gt; gains:" header followed by one mod per line.</para>
///
/// <para>Fail-closed: any deviation — child count, missing rect, missing "gain" text —
/// returns null and the caller must not click. A consumed altar drops the choice tree,
/// so "choices no longer readable" doubles as the post-click confirmation signal.</para>
/// </summary>
public static class EldritchAltarChoicesReader
{
    public static EldritchAltarChoiceSet? TryRead(MemoryReader reader, nint labelElementAddress)
    {
        if (labelElementAddress == 0) return null;
        if (!ElementReader.IsVisibleLocal(reader, labelElementAddress)) return null;
        var root = ElementReader.TryReadSnapshot(reader, labelElementAddress, maxChildren: 4);
        if (root is null || root.Children.Count != 2) return null;

        var first = TryReadChoice(reader, root.Children[0]);
        var second = TryReadChoice(reader, root.Children[1]);
        if (first is null || second is null) return null;

        return first.ClickRect.Y <= second.ClickRect.Y
            ? new EldritchAltarChoiceSet(first, second)
            : new EldritchAltarChoiceSet(second, first);
    }

    private static EldritchAltarChoice? TryReadChoice(MemoryReader reader, nint element)
    {
        if (!ElementReader.IsVisibleDeep(reader, element)) return null;
        if (ElementGeometry.TryReadRect(reader, element) is not { Width: > 10, Height: > 10 } rect)
            return null;

        var snapshot = ElementReader.TryReadSnapshot(reader, element, maxChildren: 8);
        if (snapshot is null) return null;
        foreach (var child in snapshot.Children)
        {
            var text = NativeString.Read(reader, child + KnownOffsets.Element.TextNoTags);
            if (string.IsNullOrWhiteSpace(text))
                text = NativeString.Read(reader, child + KnownOffsets.Element.Text);
            if (string.IsNullOrWhiteSpace(text)) continue;
            // The trade-off text always opens with "Player gains:" / "Eldritch Minions
            // gain:" / "Map Boss gains:" — anything else is some other subtree.
            if (!text.Contains("gain", StringComparison.OrdinalIgnoreCase)) continue;
            return new EldritchAltarChoice(rect, text);
        }
        return null;
    }
}
