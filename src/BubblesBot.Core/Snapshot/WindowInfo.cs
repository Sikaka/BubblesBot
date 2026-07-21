namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Where the PoE game window currently sits in absolute screen coords.
/// Window-relative element rects + this origin give absolute click coordinates.
/// </summary>
public readonly record struct WindowInfo(int OriginX, int OriginY, int Width, int Height)
{
    public static readonly WindowInfo Empty = new(0, 0, 0, 0);

    public bool IsValid => Width > 0 && Height > 0;

    /// <summary>Convert a window-relative point to absolute screen coords.</summary>
    public (int X, int Y) ToScreen(float windowX, float windowY)
        => (OriginX + (int)windowX, OriginY + (int)windowY);
}
