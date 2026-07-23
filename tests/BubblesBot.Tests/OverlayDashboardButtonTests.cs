using BubblesBot.Bot.Overlay;

namespace BubblesBot.Tests;

public sealed class OverlayDashboardButtonTests
{
    [Theory]
    [InlineData(12, 196, true)]
    [InlineData(371, 225, true)]
    [InlineData(11, 196, false)]
    [InlineData(372, 225, false)]
    [InlineData(12, 226, false)]
    public void HitRegionMatchesRenderedButton(int x, int y, bool expected)
    {
        Assert.Equal(expected, OverlayWindow.ContainsDashboardButton(x, y));
    }
}
