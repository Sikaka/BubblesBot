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

    [Theory]
    [InlineData(1473, 498, true)]
    [InlineData(985, 498, true)]
    [InlineData(1300, 500, false)]
    [InlineData(1900, 500, false)]
    public void GuardianSheetViewportCoversFixedRightHandNodes(
        float x, float y, bool expected)
    {
        Assert.Equal(expected,
            MapDeviceSystem.AtlasNodeInGuardianSheetViewport(x, y, 1920, 1080));
    }

    [Theory]
    [InlineData(-1, 0, true)]
    [InlineData(0, 0, true)]
    [InlineData(103, 4, true)]
    [InlineData(-1, 1, false)]
    public void AtlasPanAnchorAcceptsCanvasChildrenButRejectsExternalOverlays(
        int directChild, int tooltipLines, bool expected)
        => Assert.Equal(expected,
            MapDeviceSystem.IsSafeAtlasPanAnchor(directChild, tooltipLines));
}
