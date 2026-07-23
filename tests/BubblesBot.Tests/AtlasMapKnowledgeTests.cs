using System.Text.Json;
using BubblesBot.Bot.Knowledge;
using BubblesBot.Bot.Overlay.Navigation;
using BubblesBot.Core.Pathfinding;
using BubblesBot.Core.Snapshot;

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
                    ManualBossNotes = "Final phase gates drops.",
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
    public void AtlasGuidanceUsesOnlyConfirmedBossTransitions()
    {
        var map = new AtlasMapKnowledgeEntry
        {
            AreaId = "MapWorldsDungeon",
            AreaName = "Dungeon",
        };
        var candidate = new AtlasTransitionKnowledge { SuggestedRole = "bossArenaCandidate" };
        Assert.False(GuidanceWorker.IsKnownBossTransition(map, candidate));

        candidate.ManualRole = "bossArena";
        Assert.True(GuidanceWorker.IsKnownBossTransition(map, candidate));
    }

    [Fact]
    public void AtlasGuidanceTransfersUniqueSemanticPrototypeAcrossLayoutFingerprints()
    {
        var confirmed = new AtlasTransitionKnowledge
        {
            Key = "door|Local||old-layout",
            EntityPath = "door",
            Type = BubblesBot.Core.Game.AreaTransitionType.Local,
            ManualRole = "bossArena",
        };
        var variant = new AtlasTransitionKnowledge
        {
            Key = "door|Local||new-layout",
            EntityPath = "door",
            Type = BubblesBot.Core.Game.AreaTransitionType.Local,
        };
        var map = new AtlasMapKnowledgeEntry
        {
            AreaId = "MapWorldsDungeon",
            Transitions = { confirmed, variant },
        };

        Assert.True(GuidanceWorker.MatchesKnownBossTransition(map, variant));

        map.Transitions.Add(new AtlasTransitionKnowledge
        {
            Key = "door|Local||other-confirmed-layout",
            EntityPath = "door",
            Type = BubblesBot.Core.Game.AreaTransitionType.Local,
            ManualRole = "bossArena",
        });
        Assert.False(GuidanceWorker.MatchesKnownBossTransition(map, variant));
    }

    [Fact]
    public void AtlasGuidanceUsesSemanticTileSignatureToDisambiguatePrototypes()
    {
        var live = new AtlasTransitionKnowledge
        {
            EntityPath = "door",
            Type = BubblesBot.Core.Game.AreaTransitionType.Local,
            TileNeighborhoodSignature = "MATCH",
        };
        var map = new AtlasMapKnowledgeEntry
        {
            AreaId = "MapWorldsDungeon",
            Transitions =
            {
                new AtlasTransitionKnowledge
                {
                    EntityPath = "door",
                    Type = BubblesBot.Core.Game.AreaTransitionType.Local,
                    TileNeighborhoodSignature = "OTHER",
                    ManualRole = "bossArena",
                },
                new AtlasTransitionKnowledge
                {
                    EntityPath = "door",
                    Type = BubblesBot.Core.Game.AreaTransitionType.Local,
                    TileNeighborhoodSignature = "MATCH",
                    ManualRole = "bossArena",
                },
            },
        };

        Assert.True(GuidanceWorker.MatchesKnownBossTransition(map, live));
    }

    [Fact]
    public void AtlasGuidanceDoesNotGeneralizeAcrossMultipleSemanticTransitionFamilies()
    {
        var boss = new AtlasTransitionKnowledge
        {
            Key = "boss-key",
            EntityPath = "Metadata/MiscellaneousObjects/AreaTransition",
            Type = BubblesBot.Core.Game.AreaTransitionType.Local,
            TileNeighborhoodSignature = "BOSS-ENTRANCE",
            ManualRole = "bossArena",
        };
        var ordinaryFloor = new AtlasTransitionKnowledge
        {
            Key = "floor-key",
            EntityPath = boss.EntityPath,
            Type = boss.Type,
            TileNeighborhoodSignature = "ORDINARY-STAIRS",
        };
        var unseenFloorVariant = new AtlasTransitionKnowledge
        {
            Key = "live-key",
            EntityPath = boss.EntityPath,
            Type = boss.Type,
            TileNeighborhoodSignature = "NEW-LAYOUT",
        };
        var map = new AtlasMapKnowledgeEntry
        {
            AreaId = "MapWorldsBurialChambers",
            Transitions = { boss, ordinaryFloor },
        };

        Assert.False(GuidanceWorker.MatchesKnownBossTransition(map, unseenFloorVariant));
    }

    [Fact]
    public void SemanticTileNeighborhood_IsRotationInvariantAndKeepsNamesAndPaths()
    {
        const int tile = 23;
        var anchor = new BubblesBot.Core.Game.Vector2i { X = 10 * tile, Y = 20 * tile };
        TileKeyPosition[] original =
        [
            new("bossdoor", new() { X = anchor.X + tile, Y = anchor.Y }),
            new("Metadata/Terrain/Dungeon/boss_room.tdt",
                new() { X = anchor.X + tile, Y = anchor.Y }),
            new("pillar", new() { X = anchor.X, Y = anchor.Y + 2 * tile }),
        ];
        var rotated = original.Select(item =>
        {
            var dx = item.Position.X - anchor.X;
            var dy = item.Position.Y - anchor.Y;
            return new TileKeyPosition(item.Key,
                new() { X = anchor.X - dy, Y = anchor.Y + dx });
        });

        var first = SemanticTileNeighborhood.Compute(original, anchor);
        var second = SemanticTileNeighborhood.Compute(rotated, anchor);

        Assert.Equal(first.Signature, second.Signature);
        Assert.Contains("bossdoor", first.Keys);
        Assert.Contains("metadata/terrain/dungeon/boss_room.tdt", first.Keys);
    }

    [Fact]
    public void ComplementaryLocalTransitions_RecognizeShortSameAreaArenaPair()
    {
        var entrance = new AtlasTransitionKnowledge
        {
            EntityPath = "Metadata/MiscellaneousObjects/AreaTransition",
            Type = BubblesBot.Core.Game.AreaTransitionType.Local,
            DestinationAreaId = "",
        };
        var exit = new AtlasTransitionKnowledge
        {
            EntityPath = entrance.EntityPath,
            Type = BubblesBot.Core.Game.AreaTransitionType.Local,
            DestinationAreaId = "MapWorldsMesa",
        };

        Assert.True(AtlasMapKnowledgeObserver.IsComplementaryLocalTransitionPair(
            "MapWorldsMesa", entrance, exit));
        exit.DestinationAreaId = "MapWorldsOther";
        Assert.False(AtlasMapKnowledgeObserver.IsComplementaryLocalTransitionPair(
            "MapWorldsMesa", entrance, exit));
    }

    [Fact]
    public void Promotion_ManualKnowledgeIsImmediateAndSanitized()
    {
        var source = new AtlasMapKnowledgeDocument
        {
            Maps =
            {
                ["MapWorldsMesa"] = new AtlasMapKnowledgeEntry
                {
                    AreaId = "MapWorldsMesa",
                    AreaName = "Mesa",
                    LastObservedUtc = new DateTime(2026, 7, 22, 12, 34, 56, DateTimeKind.Utc),
                    ObservedInstanceHashes = [123456789],
                    ManualHasSeparateBossArena = true,
                    ManualBossMonsterPaths = ["Metadata/Monsters/MapBanditLeaderOak@83"],
                    ManualBossNotes = "Oak in center arena.",
                },
            },
        };

        var candidate = AtlasKnowledgePromotion.BuildCandidates(source);
        var map = candidate.Maps["MapWorldsMesa"];
        var json = JsonSerializer.Serialize(candidate, AtlasKnowledgePromotion.Json);

        Assert.True(map.Promotable);
        Assert.Equal(1, map.Confidence);
        Assert.Equal("human-confirmed map boss and arena topology", map.ConfidenceReason);
        Assert.DoesNotContain("ObservedInstanceHashes", json, StringComparison.Ordinal);
        Assert.DoesNotContain("LastObservedUtc", json, StringComparison.Ordinal);
        Assert.DoesNotContain("123456789", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Promotion_ConnectedBossRequiresThreeDistinctInstances()
    {
        var map = new AtlasMapKnowledgeEntry
        {
            AreaId = "MapWorldsDunes",
            AreaName = "Dunes",
            UniqueMonsterEvidenceInstanceHashes =
            {
                ["Metadata/Monsters/ZombieBoss/MapZombieBossHillock@83"] = [1, 2],
            },
        };
        var source = new AtlasMapKnowledgeDocument { Maps = { [map.AreaId] = map } };

        var two = AtlasKnowledgePromotion.BuildCandidates(source).Maps[map.AreaId];
        Assert.False(two.Promotable);
        Assert.Equal(2, two.EvidenceInstances);
        Assert.Equal(3, two.RequiredInstances);

        map.UniqueMonsterEvidenceInstanceHashes.Values.Single().Add(3);
        var three = AtlasKnowledgePromotion.BuildCandidates(source).Maps[map.AreaId];
        Assert.True(three.Promotable);
        Assert.Equal(0.99, three.Confidence);
    }

    [Fact]
    public void Promotion_AmbiguousSemanticFamiliesRequireFiveInstances()
    {
        var boss = new AtlasTransitionKnowledge
        {
            EntityPath = "Metadata/MiscellaneousObjects/AreaTransition",
            Type = BubblesBot.Core.Game.AreaTransitionType.Local,
            TileNeighborhoodSignature = "BOSS-FAMILY",
            TileNeighborhoodKeys = ["entrance"],
            BossMonsterPaths = ["Metadata/Monsters/BanditLeaderInt/MapBanditLeaderAlira2@83"],
            BossEvidenceInstanceHashes = [1, 2, 3, 4],
        };
        var floor = new AtlasTransitionKnowledge
        {
            EntityPath = boss.EntityPath,
            Type = boss.Type,
            TileNeighborhoodSignature = "FLOOR-FAMILY",
            TileNeighborhoodKeys = ["stairs"],
            TraversedInstanceHashes = [1, 2, 3, 4],
        };
        var map = new AtlasMapKnowledgeEntry
        {
            AreaId = "MapWorldsBurialChambers",
            Transitions = { boss, floor },
        };
        var source = new AtlasMapKnowledgeDocument { Maps = { [map.AreaId] = map } };

        var four = AtlasKnowledgePromotion.BuildCandidates(source).Maps[map.AreaId];
        Assert.False(four.Promotable);
        Assert.Equal(5, four.RequiredInstances);

        boss.BossEvidenceInstanceHashes.Add(5);
        var five = AtlasKnowledgePromotion.BuildCandidates(source).Maps[map.AreaId];
        Assert.True(five.Promotable);
    }

    [Fact]
    public void SharedCatalog_MergesTrustedKnowledgeWithoutRawEvidence()
    {
        var local = new AtlasMapKnowledgeDocument();
        var shared = new AtlasSharedKnowledgeDocument
        {
            Maps =
            {
                ["MapWorldsMesa"] = new AtlasSharedMapKnowledge
                {
                    AreaId = "MapWorldsMesa",
                    AreaName = "Mesa",
                    Promotable = true,
                    Confidence = 1,
                    HasSeparateBossArena = true,
                    BossMonsterPaths = ["Metadata/Monsters/MapBanditLeaderOak@83"],
                    Transitions =
                    {
                        new AtlasSharedTransitionKnowledge
                        {
                            EntityPath = "Metadata/MiscellaneousObjects/AreaTransition",
                            Type = BubblesBot.Core.Game.AreaTransitionType.Local,
                            TileNeighborhoodSignature = "SEMANTIC",
                            Role = "bossArena",
                            Promotable = true,
                            Confidence = 1,
                        },
                    },
                },
            },
        };

        AtlasKnowledgePromotion.ApplyShared(local, shared);

        var mesa = local.Maps["MapWorldsMesa"];
        Assert.True(mesa.ManualHasSeparateBossArena);
        Assert.Contains("Metadata/Monsters/MapBanditLeaderOak@83", mesa.ManualBossMonsterPaths);
        Assert.Equal("bossArena", Assert.Single(mesa.Transitions).ManualRole);
        Assert.Empty(mesa.ObservedInstanceHashes);
    }

    [Fact]
    public void EmbeddedCatalog_ContainsReviewedMapsOnly()
    {
        var catalog = AtlasKnowledgePromotion.LoadEmbedded();

        Assert.Contains("MapWorldsMesa", catalog.Maps.Keys);
        Assert.Contains("MapWorldsBurialChambers", catalog.Maps.Keys);
        Assert.All(catalog.Maps.Values, map => Assert.True(map.Promotable));
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
