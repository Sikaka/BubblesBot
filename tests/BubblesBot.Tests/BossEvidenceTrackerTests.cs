using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;

namespace BubblesBot.Tests;

public sealed class BossEvidenceTrackerTests
{
    private static BossObservation Boss(uint id, string path, int x, int y, int hp, int hpMax = 1000)
        => new(id, path, new Vector2i { X = x, Y = y }, hp, hpMax);

    [Fact]
    public void UnconfiguredTrackerIsNeverComplete()
    {
        var tracker = new BossEvidenceTracker();
        tracker.Observe([Boss(1, "Metadata/Monsters/Boss", 0, 0, 0)], new Vector2i { X = 0, Y = 0 }, scanHealthy: true);
        Assert.False(tracker.HasExpectedBosses);
        Assert.False(tracker.IsComplete);
    }

    [Fact]
    public void DeathAtZeroHpMarksBossDead()
    {
        var tracker = new BossEvidenceTracker();
        tracker.Configure(["BossA"]);
        tracker.Observe([Boss(1, "Metadata/Monsters/BossA/x", 10, 10, 500)], new Vector2i { X = 0, Y = 0 }, true);
        Assert.False(tracker.IsComplete);
        tracker.Observe([Boss(1, "Metadata/Monsters/BossA/x", 10, 10, 0)], new Vector2i { X = 0, Y = 0 }, true);
        Assert.True(tracker.IsComplete);
        Assert.Equal(1, tracker.BossesDead);
    }

    [Fact]
    public void DisappearanceInsideBubbleDuringHealthyScanInfersDeath()
    {
        var tracker = new BossEvidenceTracker();
        tracker.Configure(["BossA"]);
        tracker.Observe([Boss(1, "x/BossA/y", 5, 5, 800)], new Vector2i { X = 0, Y = 0 }, true);   // seen alive, near player
        tracker.Observe([], new Vector2i { X = 0, Y = 0 }, scanHealthy: true);                     // vanished, healthy scan
        Assert.True(tracker.IsComplete);
    }

    [Fact]
    public void DisappearanceDuringDegradedScanDoesNotInferDeath()
    {
        var tracker = new BossEvidenceTracker();
        tracker.Configure(["BossA"]);
        tracker.Observe([Boss(1, "x/BossA/y", 5, 5, 800)], new Vector2i { X = 0, Y = 0 }, true);
        tracker.Observe([], new Vector2i { X = 0, Y = 0 }, scanHealthy: false);
        Assert.False(tracker.IsComplete);
    }

    [Fact]
    public void MultiBossRequiresAllFragmentsDead()
    {
        var tracker = new BossEvidenceTracker();
        tracker.Configure(["BossA", "BossB"]);
        tracker.Observe([Boss(1, "x/BossA", 1, 1, 0), Boss(2, "x/BossB", 2, 2, 500)], new Vector2i { X = 0, Y = 0 }, true);
        Assert.False(tracker.IsComplete);   // only BossA dead
        tracker.Observe([Boss(2, "x/BossB", 2, 2, 0)], new Vector2i { X = 0, Y = 0 }, true);
        Assert.True(tracker.IsComplete);
    }
}
