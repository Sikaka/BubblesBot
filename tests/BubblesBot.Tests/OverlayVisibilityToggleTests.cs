using BubblesBot.Bot.Overlay;

namespace BubblesBot.Tests;

public sealed class OverlayVisibilityToggleTests
{
    [Fact]
    public void FreshKeyEdgesToggleVisibility()
    {
        var toggle = new OverlayVisibilityToggle();

        Assert.True(toggle.IsVisible);
        Assert.True(toggle.Observe(isDown: true));
        Assert.False(toggle.IsVisible);
        Assert.False(toggle.Observe(isDown: true));
        Assert.False(toggle.IsVisible);
        Assert.False(toggle.Observe(isDown: false));
        Assert.True(toggle.Observe(isDown: true));
        Assert.True(toggle.IsVisible);
    }
}
