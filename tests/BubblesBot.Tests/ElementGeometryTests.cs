using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Tests;

public sealed class ElementGeometryTests
{
    [Fact]
    public void ComposeRect_AppliesParentScrollOffsetToLeaf()
    {
        // Chains are leaf first, root last. This mirrors a virtualized specialized stash slot:
        // the child has a large local X and the parent scroll cancels most of it.
        var leaf = new Element
        {
            Position = new Vector2 { X = 1_152, Y = 0 },
            Size = new Vector2 { X = 72, Y = 72 },
            Scale = 0.675f,
        };
        var parent = new Element
        {
            Position = new Vector2 { X = 561, Y = 67 },
            ScrollOffset = new Vector2 { X = -1_152, Y = 0 },
            Scale = 0.675f,
        };

        var rect = ElementGeometry.ComposeRect([leaf, parent]);

        Assert.Equal(378.675f, rect.X, 3);
        Assert.Equal(45.225f, rect.Y, 3);
        Assert.Equal(48.6f, rect.Width, 3);
        Assert.Equal(48.6f, rect.Height, 3);
    }

    [Fact]
    public void ComposeRect_UsesEffectiveElementScale_OnTransformedAtlasCanvas()
    {
        // Captured from the live City Square atlas node. ExileCore's GetClientRectCache gave
        // (674.5512, 510.01334, 17.8875, 17.8875). The canvas scale is already effective;
        // treating it as a relative multiplier incorrectly produces Y ~= 288.
        var leaf = new Element
        {
            Position = new Vector2 { X = 1_998.6703f, Y = 2_167.941f },
            Size = new Vector2 { X = 53, Y = 53 },
            Scale = 0.3375f,
        };
        var canvas = new Element
        {
            Position = new Vector2 { X = 0, Y = -656.7902f },
            Size = new Vector2 { X = 5_689, Y = 5_689 },
            Scale = 0.3375f,
        };
        var atlas = new Element
        {
            Position = new Vector2 { X = 0, Y = 0 },
            Size = new Vector2 { X = 2_560, Y = 1_600 },
            Scale = 0.675f,
        };

        var rect = ElementGeometry.ComposeRect([leaf, canvas, atlas]);

        Assert.Equal(674.5512f, rect.X, 3);
        Assert.Equal(510.0133f, rect.Y, 3);
        Assert.Equal(17.8875f, rect.Width, 3);
        Assert.Equal(17.8875f, rect.Height, 3);
    }
}
