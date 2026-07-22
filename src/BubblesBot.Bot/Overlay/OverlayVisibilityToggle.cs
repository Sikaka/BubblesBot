namespace BubblesBot.Bot.Overlay;

/// <summary>Session-local, edge-triggered visibility state for the F12 overlay hotkey.</summary>
public sealed class OverlayVisibilityToggle
{
    public const int VirtualKey = 0x7B; // VK_F12
    private bool _wasDown;

    public bool IsVisible { get; private set; } = true;

    /// <summary>Returns true only when visibility changed on a fresh key-down edge.</summary>
    public bool Observe(bool isDown)
    {
        var changed = false;
        if (isDown && !_wasDown)
        {
            IsVisible = !IsVisible;
            changed = true;
        }
        _wasDown = isDown;
        return changed;
    }
}
