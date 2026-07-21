using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;

namespace BubblesBot.Tests;

public sealed class MapDeviceAccessPolicyTests
{
    [Fact]
    public void Chooses_device_side_farthest_from_old_portal_cluster()
    {
        var device = Pos(100, 100);
        var goal = MapDeviceAccessPolicy.Choose(
            device, Pos(100, 140), [Pos(80, 100), Pos(84, 90), Pos(84, 110)], 25);

        Assert.NotNull(goal);
        Assert.True(goal.Value.X > device.X);
        Assert.InRange(Distance(goal.Value, device), 17, 20);
    }

    [Fact]
    public void No_old_portals_requires_no_special_approach()
        => Assert.Null(MapDeviceAccessPolicy.Choose(Pos(0, 0), Pos(20, 20), [], 25));

    [Fact]
    public void Failed_device_approach_rotates_to_a_different_candidate()
    {
        var device = Pos(100, 100);
        var portals = new[] { Pos(80, 100), Pos(84, 90), Pos(84, 110) };
        var first = MapDeviceAccessPolicy.Choose(device, Pos(100, 140), portals, 25, rank: 0);
        var second = MapDeviceAccessPolicy.Choose(device, Pos(100, 140), portals, 25, rank: 1);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData(25, 37)]
    [InlineData(38, 45)]
    public void Portal_click_range_accounts_for_collision_ring(float configured, float expected)
        => Assert.Equal(expected, MapPortalEntryPolicy.ClickRange(configured));

    private static Vector2i Pos(int x, int y) => new() { X = x, Y = y };
    private static double Distance(Vector2i a, Vector2i b)
        => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
