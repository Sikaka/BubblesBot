using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;

namespace BubblesBot.Tests;

public sealed class PortalEgressPolicyTests
{
    [Fact]
    public void Live_return_ring_prefers_clear_left_side_step()
    {
        var player = Pos(328, 351);
        var stash = Pos(318, 402);
        Vector2i[] portals =
        [
            Pos(353, 358), Pos(326, 373), Pos(317, 358),
            Pos(326, 342), Pos(344, 343),
        ];

        var escape = PortalEgressPolicy.Choose(player, stash, portals);

        Assert.NotNull(escape);
        Assert.True(escape.Value.X < player.X);
        Assert.True(MinDistance(escape.Value, portals) > 15f);
    }

    [Fact]
    public void No_nearby_portal_needs_no_escape()
        => Assert.Null(PortalEgressPolicy.Choose(
            Pos(0, 0), Pos(100, 0), [Pos(200, 200)]));

    [Fact]
    public void Retry_uses_the_opposite_side_step()
    {
        var a = PortalEgressPolicy.Choose(Pos(0, 0), Pos(0, 100), [Pos(0, 15)], 0);
        var b = PortalEgressPolicy.Choose(Pos(0, 0), Pos(0, 100), [Pos(0, 15)], 1);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(-a.Value.X, b.Value.X);
    }

    private static Vector2i Pos(int x, int y) => new() { X = x, Y = y };
    private static float MinDistance(Vector2i p, IReadOnlyList<Vector2i> others)
        => others.Min(o => MathF.Sqrt((p.X - o.X) * (p.X - o.X) + (p.Y - o.Y) * (p.Y - o.Y)));
}
