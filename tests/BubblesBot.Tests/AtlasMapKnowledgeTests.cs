using System.Text.Json;
using BubblesBot.Bot.Knowledge;
using BubblesBot.Core.Pathfinding;

namespace BubblesBot.Tests;

public sealed class AtlasMapKnowledgeTests : IDisposable
{
    private readonly string _directory = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "BubblesBot.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ManualKnowledge_IsImmediatelyAvailableToRouting()
    {
        var path = Write(new AtlasMapKnowledgeDocument
        {
            Maps =
            {
                ["MapWorldsDungeon"] = new()
                {
                    AreaId = "MapWorldsDungeon",
                    AreaName = "Dungeon",
                    ManualHasSeparateBossArena = true,
                    ManualBossMonsterPaths = ["Metadata/Monsters/DungeonBoss"],
                },
            },
        });

        var knowledge = new AtlasMapKnowledgeObserver(path);

        Assert.True(knowledge.HasSeparateBossArena("Dungeon"));
        Assert.Contains("Metadata/Monsters/DungeonBoss", knowledge.BossFragments("Dungeon"));
    }

    [Fact]
    public void AutomaticKnowledge_RequiresConfirmedTransition()
    {
        var entry = new AtlasMapKnowledgeEntry
        {
            AreaId = "MapWorldsDungeon",
            AreaName = "Dungeon",
            Transitions =
            {
                new AtlasTransitionKnowledge
                {
                    Key = "opening",
                    BossMonsterPaths = ["Metadata/Monsters/DungeonBoss"],
                    BossEvidenceInstanceHashes = [1],
                    AutoConfirmed = false,
                },
            },
        };
        var path = Write(new AtlasMapKnowledgeDocument { Maps = { [entry.AreaId] = entry } });
        var candidate = new AtlasMapKnowledgeObserver(path);

        Assert.False(candidate.HasSeparateBossArena("Dungeon"));
        Assert.Empty(candidate.BossFragments("Dungeon"));

        entry.Transitions[0].BossEvidenceInstanceHashes.Add(2);
        entry.Transitions[0].AutoConfirmed = true;
        Write(new AtlasMapKnowledgeDocument { Maps = { [entry.AreaId] = entry } });
        var confirmed = new AtlasMapKnowledgeObserver(path);

        Assert.True(confirmed.HasSeparateBossArena("Dungeon"));
        Assert.Contains("Metadata/Monsters/DungeonBoss", confirmed.BossFragments("Dungeon"));
    }

    [Fact]
    public void TerrainFingerprint_IsRotationAndReflectionInvariant()
    {
        var points = new HashSet<(int X, int Y)>
        {
            (-7, -5), (-3, 2), (0, 0), (1, 8), (6, -2), (9, 4),
        };
        var original = new PatternGrid(points, transform: 0);
        var rotated = new PatternGrid(points, transform: 1);
        var reflected = new PatternGrid(points, transform: 4);
        var center = new BubblesBot.Core.Game.Vector2i { X = 100, Y = 100 };

        Assert.Equal(TerrainLandmarkSignature.Compute(original, center),
            TerrainLandmarkSignature.Compute(rotated, center));
        Assert.Equal(TerrainLandmarkSignature.Compute(original, center),
            TerrainLandmarkSignature.Compute(reflected, center));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private string Write(AtlasMapKnowledgeDocument document)
    {
        Directory.CreateDirectory(_directory);
        var path = System.IO.Path.Combine(_directory, "knowledge.json");
        File.WriteAllText(path, JsonSerializer.Serialize(document));
        return path;
    }

    private sealed class PatternGrid(HashSet<(int X, int Y)> points, int transform) : ICellReader
    {
        public int Width => 201;
        public int Height => 201;

        public int Read(int x, int y)
        {
            var dx = (x - 100) / 4;
            var dy = (y - 100) / 4;
            var source = transform switch
            {
                1 => (-dy, dx),
                4 => (-dx, dy),
                _ => (dx, dy),
            };
            return points.Contains(source) ? 5 : 0;
        }
    }
}
