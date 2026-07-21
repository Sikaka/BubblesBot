using BubblesBot.Bot.Modes;

namespace BubblesBot.Tests;

public sealed class SimulacrumRecoveryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "BubblesBot.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void RoundTripsOffscreenPendingRewardsAndLandmarks()
    {
        var store = new SimulacrumRecoveryStore(_directory);
        var expected = new SimulacrumRecoveryState(
            0xAABBCCDD, 14,
            new(309, 391), new(250, 330), new(280, 360), new(175, 310),
            [new(42, "Divination Scarab of The Cloister", 140, 280)]);

        store.Save(expected);
        var actual = store.Load(expected.AreaHash);

        Assert.NotNull(actual);
        Assert.Equal(expected.AreaHash, actual.AreaHash);
        Assert.Equal(expected.Wave, actual.Wave);
        Assert.Equal(expected.Monolith, actual.Monolith);
        Assert.Equal(expected.Stash, actual.Stash);
        Assert.Equal(expected.Portal, actual.Portal);
        Assert.Equal(expected.RewardAnchor, actual.RewardAnchor);
        Assert.Equal(expected.PendingItems, actual!.PendingItems);
    }

    [Fact]
    public void CorruptCheckpointFailsClosedWithoutThrowing()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "simulacrum-0000002A.json"), "not json");
        var store = new SimulacrumRecoveryStore(_directory);

        Assert.Null(store.Load(42));
    }

    [Fact]
    public void DeleteRemovesCheckpoint()
    {
        var store = new SimulacrumRecoveryStore(_directory);
        var state = new SimulacrumRecoveryState(7, 1, null, null, null, null, []);
        store.Save(state);

        store.Delete(7);

        Assert.Null(store.Load(7));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
