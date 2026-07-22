using BubblesBot.Core.Pathfinding;

namespace BubblesBot.Tests;

public sealed class TerrainCellReaderTests
{
    [Fact]
    public void DecodePackedCell_ReadsLowAndHighNibbles()
    {
        byte[] packed = [0x21, 0xF0, 0xA5];

        Assert.Equal(1, TerrainCellReader.DecodePackedCell(packed, 0));
        Assert.Equal(2, TerrainCellReader.DecodePackedCell(packed, 1));
        Assert.Equal(0, TerrainCellReader.DecodePackedCell(packed, 2));
        Assert.Equal(15, TerrainCellReader.DecodePackedCell(packed, 3));
        Assert.Equal(5, TerrainCellReader.DecodePackedCell(packed, 4));
        Assert.Equal(10, TerrainCellReader.DecodePackedCell(packed, 5));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(6)]
    [InlineData(7)]
    public void DecodePackedCell_OutOfBounds_ReturnsBlocked(int cellIndex)
    {
        byte[] packed = [0x21, 0x43, 0x65];

        Assert.Equal(0, TerrainCellReader.DecodePackedCell(packed, cellIndex));
    }
}
