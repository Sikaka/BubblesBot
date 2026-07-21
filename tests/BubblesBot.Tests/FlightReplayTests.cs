using System.Text.Json;
using BubblesBot.Bot.Diagnostics;
using BubblesBot.Core.Game;
using BubblesBot.Core.Knowledge;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Tests;

public sealed class FlightReplayTests
{
    [Fact]
    public void ReconstructsDeltaAndUsesProductionTargetPolicy()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bubbles-replay-{Guid.NewGuid():N}.jsonl");
        try
        {
            var valid = Entity(1, 10, ObservationTruth.True);
            var unknown = Entity(2, 5, ObservationTruth.Unknown);
            var lines = new[]
            {
                JsonSerializer.Serialize(new
                {
                    kind = "world.keyframe", TickId = 1L, MonotonicTimestamp = 100L,
                    AreaHash = 7u, ModeId = 4, Mode = "Map farming",
                    Player = new FlightPlayer(0, 0, 100, 100),
                    changedEntities = new[] { valid, unknown }, removedEntityIds = Array.Empty<uint>(),
                }),
                JsonSerializer.Serialize(new
                {
                    kind = "world.delta", TickId = 2L, MonotonicTimestamp = 200L,
                    AreaHash = 7u, ModeId = 4, Mode = "Map farming",
                    Player = new FlightPlayer(0, 0, 100, 100),
                    changedEntities = Array.Empty<FlightEntity>(), removedEntityIds = new[] { 1u },
                }),
            };
            File.WriteAllLines(path, lines);

            var intents = FlightReplay.Run(path);

            Assert.Equal(2, intents.Count);
            Assert.Equal("attack", intents[0].Kind);
            Assert.Equal(1u, intents[0].TargetId);
            Assert.Equal("explore", intents[1].Kind);
            Assert.Contains("TargetabilityUnknown=1", intents[1].Evidence);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void StopAtTickProducesInspectablePrefix()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bubbles-replay-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllLines(path, Enumerable.Range(1, 3).Select(tick => JsonSerializer.Serialize(new
            {
                kind = tick == 1 ? "world.keyframe" : "world.delta",
                TickId = (long)tick,
                MonotonicTimestamp = (long)tick * 100,
                AreaHash = 7u,
                ModeId = 4,
                Mode = "Map farming",
                Player = new FlightPlayer(0, 0, 100, 100),
                changedEntities = tick == 1 ? new[] { Entity(1, 10, ObservationTruth.True) } : Array.Empty<FlightEntity>(),
                removedEntityIds = Array.Empty<uint>(),
            })));

            var intents = FlightReplay.Run(path, stopAtTick: 2);

            Assert.Equal(new long[] { 1, 2 }, intents.Select(x => x.TickId));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReconstructsMechanicObservations()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bubbles-replay-{Guid.NewGuid():N}.jsonl");
        try
        {
            var ritual = new FlightEntity(
                9, "Metadata/Terrain/Leagues/Ritual/RitualRuneInteractable", 10, 20, 1, 1,
                ObservationTruth.True, ObservationTruth.Unknown, ObservationTruth.Unknown,
                ObservationTruth.True, false, false, EntityListReader.EntityKind.Unknown,
                EntityDisposition.Ignore, EntityCache.Tier.Hot,
                ObservationTruth.Unknown, true, 1, true, 1);
            File.WriteAllLines(path,
            [
                JsonSerializer.Serialize(new
                {
                    kind = "world.keyframe", TickId = 1L, MonotonicTimestamp = 100L,
                    AreaHash = 7u, ModeId = 4, Mode = "Map farming",
                    Player = new FlightPlayer(0, 0, 100, 100),
                    changedEntities = new[] { ritual }, removedEntityIds = Array.Empty<uint>(),
                }),
            ]);

            var replayed = FlightReplay.ReadFrames(path).Single().Entities.Single();

            Assert.True(replayed.RitualCurrentStateKnown);
            Assert.Equal(1, replayed.RitualCurrentState);
            Assert.True(replayed.RitualInteractionEnabledKnown);
            Assert.Equal(1, replayed.RitualInteractionEnabled);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static FlightEntity Entity(uint id, int x, ObservationTruth targetable) => new(
        id, $"Metadata/Monsters/Test{id}", x, 0, 100, 100,
        targetable, ObservationTruth.False, ObservationTruth.False, ObservationTruth.True,
        false, false, EntityListReader.EntityKind.Monster, EntityDisposition.Combatant, EntityCache.Tier.Hot);
}
