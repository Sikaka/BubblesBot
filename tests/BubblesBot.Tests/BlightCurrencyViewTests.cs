using BubblesBot.Core.Snapshot;

namespace BubblesBot.Tests;

public sealed class BlightCurrencyViewTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData("1,234", 1234)]
    [InlineData(" 900 ", 900)]
    public void Parses_visible_hud_currency(string text, int expected)
    {
        Assert.True(BlightCurrencyView.TryParse(text, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--")]
    [InlineData("-1")]
    public void Rejects_unknown_or_invalid_currency(string text)
        => Assert.False(BlightCurrencyView.TryParse(text, out _));
}
