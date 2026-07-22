using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Tests;

public sealed class BossCheckpointPortalSystemTests
{
    [Fact]
    public void ConfirmsOnlyFreshTownPortalNearCaster()
    {
        var player = new Vector2i { X = 100, Y = 100 };
        Assert.True(BossCheckpointPortalSystem.HasUsablePortal(
            [Portal(118, 100, EntityListReader.EntityKind.TownPortal)], player));
        Assert.False(BossCheckpointPortalSystem.HasUsablePortal(
            [Portal(150, 100, EntityListReader.EntityKind.TownPortal)], player));
        Assert.False(BossCheckpointPortalSystem.HasUsablePortal(
            [Portal(118, 100, EntityListReader.EntityKind.Portal)], player));

        var stale = Portal(118, 100, EntityListReader.EntityKind.TownPortal);
        stale.MissedWalks = 1;
        Assert.False(BossCheckpointPortalSystem.HasUsablePortal([stale], player));
    }

    private static EntityCache.Entry Portal(
        int x, int y, EntityListReader.EntityKind kind) => new()
    {
        Kind = kind,
        GridPosition = new Vector2i { X = x, Y = y },
    };
}
