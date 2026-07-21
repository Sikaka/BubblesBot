using BubblesBot.Core.Game;
using BubblesBot.Research.Probing;

namespace BubblesBot.Research.Probes.Player;

/// <summary>
/// Player Positioned.GridPosition and Render.Pos. Both are volatile (you move), so without an
/// oracle they are range-checked rather than equality-checked.
/// </summary>
public sealed class PositionProbe : IProbe
{
    public string Name => "player.position";
    public string Group => "player";
    public string Description => "Positioned grid + Render world pos are live/plausible.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx)
    {
        var positioned = ctx.Chain.PlayerComponent("Positioned");
        var render = ctx.Chain.PlayerComponent("Render");

        ProbeResult grid;
        if (positioned != 0 && ctx.Reader.TryReadStruct<Vector2i>(positioned + KnownOffsets.PositionedComponent.GridPosition, out var g))
            grid = ProbeResult.Combine(
                Check.Live(ctx, "player.grid.x", g.X, "Positioned.Grid.X", -50_000, 50_000),
                Check.Live(ctx, "player.grid.y", g.Y, "Positioned.Grid.Y", -50_000, 50_000));
        else
            grid = ProbeResult.Fail("Positioned.GridPosition unreadable");

        ProbeResult worldPos;
        if (render != 0 && ctx.Reader.TryReadStruct<Vector3>(render + KnownOffsets.RenderComponent.Pos, out var p))
            worldPos = float.IsFinite(p.X) && float.IsFinite(p.Y) && Math.Abs(p.X) < 1e7 && Math.Abs(p.Y) < 1e7
                ? ProbeResult.Pass($"Render.Pos = ({p.X:0},{p.Y:0},{p.Z:0})")
                : ProbeResult.Fail($"Render.Pos implausible ({p.X},{p.Y},{p.Z})");
        else
            worldPos = ProbeResult.Fail("Render.Pos unreadable");

        return ProbeResult.Combine(grid, worldPos);
    }

    public ProbeResult Discover(ProbeContext ctx)
    {
        var positioned = ctx.Chain.PlayerComponent("Positioned");
        if (positioned == 0) return ProbeResult.Found("Positioned.GridPosition", []);
        return Discovery.IntValue(ctx, positioned, "player.grid.x", 0x340, "Positioned.Grid.X");
    }
}
