using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Computes the window-relative rectangle of a UI <see cref="Element"/> by walking up its
/// parent chain. Mirrors the math ExileCore uses: each parent contributes its position at that
/// element's effective scale. The result is in client (window) coordinates and matches what the
/// game draws on screen.
/// </summary>
public static class ElementGeometry
{
    private const int MaxParentDepth = 32;

    public readonly record struct Rect(float X, float Y, float Width, float Height)
    {
        public float CenterX => X + Width  * 0.5f;
        public float CenterY => Y + Height * 0.5f;

        public bool IntersectsWindow(int windowWidth, int windowHeight)
            => Width > 0 && Height > 0
            && X + Width  > 0 && X < windowWidth
            && Y + Height > 0 && Y < windowHeight;

        public bool Contains(float x, float y)
            => x >= X && x < X + Width && y >= Y && y < Y + Height;

        public bool Overlaps(in Rect other)
            => X < other.X + other.Width && other.X < X + Width
            && Y < other.Y + other.Height && other.Y < Y + Height;
    }

    /// <summary>
    /// Read the element's rect in window-relative coords. Returns null on bad reads or
    /// implausible parent chains.
    /// </summary>
    public static Rect? TryReadRect(MemoryReader reader, nint elementAddress)
    {
        if (!LooksLikeUserAddress(elementAddress))
            return null;
        if (!reader.TryReadStruct<Element>(elementAddress, out var leaf))
            return null;

        // Walk parents accumulating position. Collect the chain first so it can be applied
        // root-to-leaf just like ExileCore's GetClientRectCache.
        Span<Element> chain = stackalloc Element[MaxParentDepth];
        var depth = 0;
        chain[depth++] = leaf;

        var p = leaf.Parent;
        while (p != 0 && depth < MaxParentDepth)
        {
            if (!LooksLikeUserAddress(p)) return null;
            if (!reader.TryReadStruct<Element>(p, out var parent)) return null;
            chain[depth++] = parent;
            p = parent.Parent;
            if (p == chain[depth - 1].SelfPointer) break; // root self-loop
        }

        // Root → leaf accumulation. Position is in parent-local units; scale compounds.
        return ComposeRect(chain[..depth]);
    }

    /// <summary>
    /// Composes a leaf-to-root element chain. A parent's scroll offset translates all of its
    /// descendants; specialized stash tabs rely on this to virtualize slots outside the panel.
    /// </summary>
    internal static Rect ComposeRect(ReadOnlySpan<Element> chain)
    {
        if (chain.IsEmpty)
            return default;

        // Element.Scale is already the element's effective UI scale, not a local transform to
        // multiply into its parent's value. This distinction matters on transformed canvases:
        // the atlas root was 0.675 while its panned canvas and nodes were 0.3375. Compounding
        // those values put the live City Square node hundreds of pixels above its rendered
        // position. Zero/invalid scales inherit the last valid parent scale defensively.
        float scale = 1f;
        float x = 0f, y = 0f;

        for (var i = chain.Length - 1; i >= 0; i--)
        {
            var e = chain[i];
            if (e.Scale > 0)
                scale = e.Scale;
            x += e.Position.X * scale;
            y += e.Position.Y * scale;

            // The element's scroll offset affects children, but not its own rectangle.
            if (i > 0)
            {
                x += e.ScrollOffset.X * scale;
                y += e.ScrollOffset.Y * scale;
            }
        }

        var w = chain[0].Size.X * scale;
        var h = chain[0].Size.Y * scale;
        return new Rect(x, y, w, h);
    }

    private static bool LooksLikeUserAddress(nint p)
    {
        var v = (long)p;
        return v > 0x10000 && v < 0x7FFF_FFFF_FFFF;
    }
}
