using BubblesBot.Core.Game;

namespace BubblesBot.Research.Validation.Tests;

public sealed class TerrainPackedGridOracleTest : ValidationTest
{
    public override string Name => "Terrain packed path/target grids";
    public override string? Group => "Terrain grids";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(StateKeys.IngameData, out var dataObj) || dataObj is not nint ingameData)
            return new TestOutcome.Skip(Name, "IngameData not resolved");

        if (!TerrainGridReader.TryReadSnapshot(ctx.Reader, ingameData, out var terrain))
            return new TestOutcome.Fail(Name, "could not read packed terrain grid snapshot");

        var truth = await ctx.Poemcp.EvalAsync(
            """
            var p = Player.GridPosNum;
            ((int)p.X) + "," + ((int)p.Y) + "|" +
            IngameState.Data.RawPathfindingData.Length + "," + IngameState.Data.RawPathfindingData[0].Length + "|" +
            IngameState.Data.GetPathfindingValueAt(p) + "|" +
            IngameState.Data.GetTerrainTargetingValueAt(p)
            """, ct);
        if (!truth.Success)
            return new TestOutcome.Skip(Name, $"POEMCP unavailable: {truth.Error}");

        var parts = truth.AsString().Split('|');
        if (parts.Length != 4)
            return new TestOutcome.Fail(Name, $"unexpected POEMCP result: {truth.AsString()}");

        var gridParts = parts[0].Split(',', 2);
        var grid = new Vector2i { X = int.Parse(gridParts[0]), Y = int.Parse(gridParts[1]) };
        var dimParts = parts[1].Split(',', 2);
        var truthRows = int.Parse(dimParts[0]);
        var truthColumns = int.Parse(dimParts[1]);
        var truthPath = int.Parse(parts[2]);
        var truthTarget = int.Parse(parts[3]);

        if (terrain.Rows != truthRows || terrain.Columns != truthColumns)
            return new TestOutcome.Fail(Name, $"dims mismatch ours={terrain.Rows},{terrain.Columns} POEMCP={truthRows},{truthColumns}");
        if (!TerrainGridReader.TryGetPathfindingValue(ctx.Reader, terrain, grid, out var path))
            return new TestOutcome.Fail(Name, $"could not read path value at {grid.X},{grid.Y}");
        if (!TerrainGridReader.TryGetTerrainTargetingValue(ctx.Reader, terrain, grid, out var target))
            return new TestOutcome.Fail(Name, $"could not read terrain target value at {grid.X},{grid.Y}");

        if (path != truthPath)
            return new TestOutcome.Fail(Name, $"path mismatch at {grid.X},{grid.Y}: ours={path}, POEMCP={truthPath}");
        if (target != truthTarget)
            return new TestOutcome.Fail(Name, $"target mismatch at {grid.X},{grid.Y}: ours={target}, POEMCP={truthTarget}");

        return new TestOutcome.Pass(Name, $"dims={terrain.Rows}x{terrain.Columns}, playerGrid=({grid.X},{grid.Y}), path={path}, target={target}");
    }
}
