namespace BubblesBot.Bot.Overlay;

/// <summary>
/// Session-local, edge-triggered state for the F12 hotkey. Toggles the operator HUD "chrome"
/// (status panel, open-web-UI button, top guidance banner, update notice) — NOT the whole
/// overlay. The informational map hack, guidance routes, and HP bars keep drawing regardless,
/// so F12 clears the screen for active play without disabling overlay functionality.
/// </summary>
public sealed class OverlayVisibilityToggle
{
    public const int VirtualKey = 0x7B; // VK_F12
    private bool _wasDown;

    /// <summary>True when the HUD chrome is shown. Starts visible; F12 flips it.</summary>
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
