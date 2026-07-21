using BubblesBot.Core.Game;

namespace BubblesBot.Tests;

public sealed class StashReaderTests
{
    [Fact]
    public void MaximumTabElements_CoversLargeStandardStash()
    {
        Assert.True(StashReader.MaximumTabElements >= 273);
    }
}
