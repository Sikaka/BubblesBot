using BubblesBot.Bot.Modes;
using BubblesBot.Core.Game;
using BubblesBot.Core.Knowledge;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Tests;

public sealed class BlightPositioningTests
{
    [Fact]
    public void Return_portal_filter_rejects_blight_lane_spawners()
    {
        var lane = new EntityCache.Entry
        {
            Kind = EntityListReader.EntityKind.Portal,
            Path = "Metadata/Terrain/Leagues/Blight/Objects/BlightPortal",
        };
        var exit = new EntityCache.Entry
        {
            Kind = EntityListReader.EntityKind.TownPortal,
            Path = "Metadata/MiscellaneousObjects/MultiplexPortal",
        };

        Assert.False(BlightMode.IsMapReturnPortal(lane));
        Assert.True(BlightMode.IsMapReturnPortal(exit));
    }

    [Fact]
    public void Cleanup_approach_stops_short_of_hostile()
    {
        var goal = BlightPositioning.ApproachAtStandoff(Pos(0, 0), Pos(100, 0), 30);

        Assert.Equal(Pos(70, 0), goal);
        Assert.Equal(30, Distance(goal, Pos(100, 0)));
    }

    [Fact]
    public void Cleanup_approach_holds_when_already_near_hostile()
    {
        var player = Pos(82, 9);

        var goal = BlightPositioning.ApproachAtStandoff(player, Pos(100, 0), 30);

        Assert.Equal(player, goal);
    }

    [Fact]
    public void Hazard_near_player_moves_to_opposite_side_of_pump()
    {
        var hazard = Entry(10, 10, 0);
        hazard.Disposition = EntityDisposition.Hazard;

        var goal = BlightPositioning.Choose(Pos(0, 0), Pos(5, 0), 40, [hazard]);

        Assert.NotNull(goal);
        Assert.StartsWith("avoid-hazard", goal.Value.Reason);
        Assert.True(goal.Value.Position.X < 0);
        Assert.InRange(Distance(Pos(0, 0), goal.Value.Position), 20, 24);
    }

    [Fact]
    public void Outside_leash_returns_to_pump()
    {
        var goal = BlightPositioning.Choose(Pos(0, 0), Pos(50, 0), 40, []);

        Assert.Equal(Pos(0, 0), goal?.Position);
        Assert.Equal("outside-defend-radius", goal?.Reason);
    }

    [Fact]
    public void Closest_pump_threat_is_intercepted_inside_leash()
    {
        var nearPump = Entry(20, 70, 0);
        var closerToPlayerButSaferForPump = Entry(21, -90, 0);

        var goal = BlightPositioning.Choose(
            Pos(0, 0), Pos(-60, 0), 80, [nearPump, closerToPlayerButSaferForPump]);

        Assert.NotNull(goal);
        Assert.StartsWith("guard-closest-lane:20", goal.Value.Reason);
        Assert.InRange(goal.Value.Position.X, 34, 36);
    }

    [Fact]
    public void Common_monster_inside_danger_radius_controls_idle_side()
    {
        var common = Entry(30, 20, 20);

        var goal = BlightPositioning.Choose(Pos(0, 0), Pos(-10, 0), 40, [common]);

        Assert.NotNull(goal);
        Assert.StartsWith("guard-closest-lane:30", goal.Value.Reason);
        Assert.True(goal.Value.Position.X > 0);
        Assert.True(goal.Value.Position.Y > 0);
        Assert.InRange(Distance(Pos(0, 0), goal.Value.Position), 14, 17);
    }

    private static EntityCache.Entry Entry(uint id, int x, int y) => new()
    {
        Id = id,
        Kind = EntityListReader.EntityKind.Monster,
        GridPosition = Pos(x, y),
        Disposition = EntityDisposition.Combatant,
        HpCurrent = 100,
        HpMax = 100,
        LifeReadable = BooleanObservation.Known(true, "Life", 1, ObservationConfidence.Validated),
        Targetability = BooleanObservation.Known(true, "Targetable", 1, ObservationConfidence.Validated),
        AlliedReaction = BooleanObservation.Known(false, "Allied", 1, ObservationConfidence.Validated),
        Dormancy = BooleanObservation.Known(false, "Dormant", 1, ObservationConfidence.Validated),
    };

    private static Vector2i Pos(int x, int y) => new() { X = x, Y = y };
    private static double Distance(Vector2i a, Vector2i b)
        => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
