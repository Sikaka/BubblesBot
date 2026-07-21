using BubblesBot.Bot.Strategies;
using BubblesBot.Bot.Systems;

namespace BubblesBot.Tests;

public sealed class AtlasNodeSelectionTests
{
    [Fact]
    public void StrandUsesCurrentPostPatchCanvasChild()
    {
        Assert.True(AtlasNodeCatalog.TryGetDataIndex("Strand", out var dataIndex));
        Assert.Equal(34, dataIndex);
        Assert.Equal(36, dataIndex + MapDeviceSystem.CurrentAtlasNodeUiPrefix);
    }

    [Theory]
    [InlineData(606, 244, true)]
    [InlineData(1192, 221, false)]
    [InlineData(1300, 500, false)]
    [InlineData(-10, 500, false)]
    [InlineData(900, 950, false)]
    public void SafeAtlasViewportExcludesInventoryAndOffscreenCoordinates(
        float x, float y, bool expected)
    {
        Assert.Equal(expected, MapDeviceSystem.AtlasNodeInSafeViewport(x, y, 1920, 1080));
    }
}
