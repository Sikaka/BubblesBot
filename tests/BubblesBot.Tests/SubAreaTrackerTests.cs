using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;

namespace BubblesBot.Tests;

public sealed class SubAreaTrackerTests
{
    [Fact]
    public void NewSubAreaThenReturnToParentIsClassified()
    {
        var tracker = new SubAreaTracker();
        tracker.EnterParent(0xAAAA);
        tracker.TakingTransition(new Vector2i { X = 100, Y = 200 });

        Assert.Equal(SubAreaArrival.NewSubArea, tracker.Classify(0xBBBB, AreaRole.SubArea));
        Assert.True(tracker.InSubArea);
        Assert.Equal(new Vector2i { X = 100, Y = 200 }, tracker.ReturnAnchor);

        Assert.Equal(SubAreaArrival.ParentMap, tracker.Classify(0xAAAA, AreaRole.Map));
        Assert.False(tracker.InSubArea);
    }

    [Fact]
    public void BossArenaCountsAsNewSubArea()
    {
        var tracker = new SubAreaTracker();
        tracker.EnterParent(0x1);
        Assert.Equal(SubAreaArrival.NewSubArea, tracker.Classify(0x2, AreaRole.BossArena));
    }

    [Fact]
    public void SafeHubArrivalIsAnUnexpectedExit()
    {
        var tracker = new SubAreaTracker();
        tracker.EnterParent(0x1);
        Assert.Equal(SubAreaArrival.LeftToHub, tracker.Classify(0x9, AreaRole.SafeHub));
    }

    [Fact]
    public void UnknownRoleIsUnresolved()
    {
        var tracker = new SubAreaTracker();
        tracker.EnterParent(0x1);
        Assert.Equal(SubAreaArrival.Unknown, tracker.Classify(0x2, AreaRole.Unknown));
    }
}
