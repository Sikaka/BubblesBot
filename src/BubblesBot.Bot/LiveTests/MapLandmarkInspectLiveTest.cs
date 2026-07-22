using System.Text.Json;
using BubblesBot.Bot.Knowledge;
using BubblesBot.Core;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Read-only atlas-map landmark capture for passive knowledge research.</summary>
public sealed class MapLandmarkInspectLiveTest : ILiveTestCase
{
    public string Id => "map-landmark-inspect";
    public string Name => "Map transition and landmark inspection";
    public string Description => "Captures typed transitions, unique monsters, tile markers, tile keys, and invariant local terrain signatures.";
    public string ManualSetup => "Stand near a suspected map landmark or boss-room opening.";
    public LiveTestMutation Mutation => LiveTestMutation.ReadOnly;
    public bool DrivesInput => false;

    public Task<LiveTestCaseResult> RunAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = context.Snapshot();
        if (snapshot.Player is not { } player)
            return Task.FromResult(LiveTestCaseResult.Blocked("player unavailable", "PlayerMissing"));

        var (areaId, areaName) = ReadAreaIdentity(context.Reader, context.IngameDataAddress);
        var entities = ReadEntities(context.Reader, context.IngameDataAddress);
        var transitions = entities
            .Where(entity => entity.Kind == EntityListReader.EntityKind.AreaTransition)
            .Select(entity => DescribeTransition(context.Reader, snapshot, player.GridPosition, entity))
            .ToArray();
        var uniques = entities
            .Where(entity => entity.Kind == EntityListReader.EntityKind.Monster
                && entity.Rarity == EntityListReader.EntityRarity.Unique)
            .Select(entity => new
            {
                entity.Id,
                entity.Path,
                Grid = entity.GridPosition,
                Hp = entity.Health?.Current,
                HpMax = entity.Health?.Max,
                entity.IsTargetable,
            })
            .ToArray();

        var tileMap = snapshot.TileMap;
        var interestingKeys = tileMap.Keys
            .Where(key => ContainsLandmarkToken(key))
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var tileEntities = snapshot.TileEntities;
        var interestingTileEntities = tileEntities.Entries
            .Where(entry => ContainsLandmarkToken(entry.Path))
            .Select(entry => new
            {
                entry.Path,
                Grid = entry.TileGridPosition,
                Distance = Distance(player.GridPosition, entry.TileGridPosition),
            })
            .OrderBy(entry => entry.Distance)
            .ToArray();

        var document = new
        {
            CapturedUtc = DateTime.UtcNow,
            snapshot.AreaHash,
            AreaId = areaId,
            AreaName = areaName,
            PlayerGrid = player.GridPosition,
            PlayerTerrainSignature = TerrainLandmarkSignature.Compute(snapshot.Nav.PathReader, player.GridPosition),
            Transitions = transitions,
            UniqueMonsters = uniques,
            Tiles = new
            {
                tileMap.TileCount,
                tileMap.Columns,
                tileMap.Rows,
                tileMap.LoadError,
                KeyCount = tileMap.Keys.Count,
                InterestingKeys = interestingKeys,
            },
            TileEntities = new
            {
                tileEntities.TileCount,
                tileEntities.Columns,
                tileEntities.LoadError,
                Total = tileEntities.Entries.Count,
                Interesting = interestingTileEntities,
            },
        };
        var artifact = Path.Combine(context.EvidenceDirectory, "map-landmarks.json");
        File.WriteAllText(artifact, JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            WriteIndented = true,
            IncludeFields = true,
        }));

        context.Observe("area identity", $"id='{areaId}' name='{areaName}' hash=0x{snapshot.AreaHash:X8}");
        context.Observe("typed transitions", transitions.Length == 0
            ? "none"
            : string.Join(" | ", transitions.Select(transition => transition.Summary)));
        context.Observe("unique monsters", uniques.Length == 0
            ? "none"
            : string.Join(" | ", uniques.Select(unique => $"{unique.Id}:{unique.Path} hp={unique.Hp}/{unique.HpMax}")));
        context.Observe("tile metadata",
            $"tiles={tileMap.TileCount} keys={tileMap.Keys.Count} interesting={interestingKeys.Length} error='{tileMap.LoadError}'");
        context.Observe("tile entities",
            $"total={tileEntities.Entries.Count} interesting={interestingTileEntities.Length} error='{tileEntities.LoadError}'");
        context.Observe("artifact", artifact);

        var ok = true;
        ok &= context.Check(snapshot.AreaHash != 0, "area hash", $"0x{snapshot.AreaHash:X8}");
        ok &= context.Check(!string.IsNullOrWhiteSpace(areaId), "raw area id", areaId);
        ok &= context.Check(transitions.Length > 0, "area transitions", $"count={transitions.Length}");
        return Task.FromResult(ok
            ? LiveTestCaseResult.Pass("map landmark evidence captured", "MapLandmarksCaptured")
            : LiveTestCaseResult.Fail("map landmark capture incomplete", "MapLandmarkReadIncomplete"));
    }

    private static TransitionCapture DescribeTransition(
        MemoryReader reader,
        GameSnapshot snapshot,
        Vector2i player,
        EntityListReader.EntitySnapshot entity)
    {
        AreaTransitionIdentity? identity = null;
        if (entity.Components.TryGetValue("AreaTransition", out var component)
            && AreaTransitionIdentityReader.TryRead(reader, component, out var value))
            identity = value;
        var grid = entity.GridPosition ?? default;
        var signature = TerrainLandmarkSignature.Compute(snapshot.Nav.PathReader, grid);
        var summary = $"id={entity.Id} path='{entity.Path}' grid=({grid.X},{grid.Y}) d={Distance(player, grid):F1} "
            + $"type={identity?.Type.ToString() ?? "unknown"} destination='{identity?.DestinationAreaId}'/'{identity?.DestinationAreaName}' sig={signature}";
        return new TransitionCapture(
            summary, entity.Id, entity.Path, grid, Distance(player, grid),
            identity?.AreaId, identity?.Type, identity?.DestinationAreaId ?? "",
            identity?.DestinationAreaName ?? "", signature,
            entity.Components.Keys.OrderBy(key => key).ToArray());
    }

    private static EntityListReader.EntitySnapshot[] ReadEntities(MemoryReader reader, nint ingameData)
    {
        if (!reader.TryReadStruct<nint>(ingameData + KnownOffsets.IngameData.EntityList, out var list) || list == 0)
            return [];
        return EntityListReader.EnumerateEntityAddresses(reader, list).EntityAddresses
            .Select(address => EntityListReader.TryReadSnapshot(reader, address))
            .Where(entity => entity is not null)
            .Select(entity => entity!)
            .ToArray();
    }

    private static (string Id, string Name) ReadAreaIdentity(MemoryReader reader, nint ingameData)
    {
        if (!reader.TryReadStruct<nint>(ingameData + KnownOffsets.IngameData.CurrentArea, out var area) || area == 0)
            return ("", "");
        return (ReadPointerString(reader, area + KnownOffsets.WorldArea.Id, 64),
            ReadPointerString(reader, area + KnownOffsets.WorldArea.Name, 128));
    }

    private static string ReadPointerString(MemoryReader reader, nint address, int maxChars)
    {
        try
        {
            return reader.TryReadStruct<nint>(address, out var pointer) && pointer != 0
                ? reader.ReadStringUtf16(pointer, maxChars).TrimEnd('\0')
                : "";
        }
        catch { return ""; }
    }

    private static bool ContainsLandmarkToken(string value)
        => value.Contains("transition", StringComparison.OrdinalIgnoreCase)
            || value.Contains("arena", StringComparison.OrdinalIgnoreCase)
            || value.Contains("boss", StringComparison.OrdinalIgnoreCase)
            || value.Contains("door", StringComparison.OrdinalIgnoreCase);

    private static double Distance(Vector2i a, Vector2i b)
    {
        long dx = a.X - b.X;
        long dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private sealed record TransitionCapture(
        string Summary,
        uint EntityId,
        string EntityPath,
        Vector2i Grid,
        double Distance,
        ushort? AreaId,
        AreaTransitionType? Type,
        string DestinationAreaId,
        string DestinationAreaName,
        string TerrainSignature,
        string[] Components);
}
