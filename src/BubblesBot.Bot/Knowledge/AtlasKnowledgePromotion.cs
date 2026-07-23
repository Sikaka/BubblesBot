using System.Text.Json;
using System.Text.Json.Serialization;
using BubblesBot.Core.Game;

namespace BubblesBot.Bot.Knowledge;

/// <summary>
/// Converts private observation evidence into a sanitized, reviewable catalog. Raw area hashes and
/// timestamps never exist in the shared schema. Runtime loads the embedded reviewed catalog first,
/// then overlays local evidence/manual corrections.
/// </summary>
internal static class AtlasKnowledgePromotion
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal static string DefaultObservationPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BubblesBot", "map-knowledge", "atlas-map-observations.json");

    internal static string DefaultCandidatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BubblesBot", "map-knowledge", "atlas-map-candidates.json");

    internal static AtlasMapKnowledgeDocument LoadMerged(string observationPath)
    {
        var local = LoadObservations(observationPath);
        ApplyShared(local, LoadEmbedded());
        return local;
    }

    internal static AtlasMapKnowledgeDocument LoadObservations(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<AtlasMapKnowledgeDocument>(File.ReadAllText(path), Json)
                    ?? new AtlasMapKnowledgeDocument()
                : new AtlasMapKnowledgeDocument();
        }
        catch
        {
            return new AtlasMapKnowledgeDocument();
        }
    }

    internal static AtlasSharedKnowledgeDocument LoadEmbedded()
    {
        try
        {
            var assembly = typeof(AtlasKnowledgePromotion).Assembly;
            var resource = assembly.GetManifestResourceNames().SingleOrDefault(name =>
                name.EndsWith(".Resources.atlas-map-knowledge.json", StringComparison.Ordinal));
            if (resource is null) return new AtlasSharedKnowledgeDocument();
            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null) return new AtlasSharedKnowledgeDocument();
            return JsonSerializer.Deserialize<AtlasSharedKnowledgeDocument>(stream, Json)
                ?? new AtlasSharedKnowledgeDocument();
        }
        catch
        {
            return new AtlasSharedKnowledgeDocument();
        }
    }

    internal static void ApplyShared(
        AtlasMapKnowledgeDocument target,
        AtlasSharedKnowledgeDocument shared)
    {
        foreach (var (areaId, source) in shared.Maps)
        {
            if (!source.Promotable) continue;
            if (!target.Maps.TryGetValue(areaId, out var map))
            {
                map = new AtlasMapKnowledgeEntry { AreaId = source.AreaId, AreaName = source.AreaName };
                target.Maps[areaId] = map;
            }
            if (map.AreaName.Length == 0) map.AreaName = source.AreaName;
            map.ManualHasSeparateBossArena ??= source.HasSeparateBossArena;
            AddDistinct(map.ManualBossMonsterPaths, source.BossMonsterPaths);
            if (map.ManualBossNotes.Length == 0) map.ManualBossNotes = source.BossNotes;

            foreach (var transition in source.Transitions.Where(item => item.Promotable))
            {
                var existing = map.Transitions.FirstOrDefault(candidate =>
                    candidate.EntityPath.Equals(transition.EntityPath, StringComparison.OrdinalIgnoreCase)
                    && candidate.Type == transition.Type
                    && candidate.DestinationAreaId.Equals(
                        transition.DestinationAreaId, StringComparison.OrdinalIgnoreCase)
                    && candidate.TileNeighborhoodSignature.Equals(
                        transition.TileNeighborhoodSignature, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    existing = new AtlasTransitionKnowledge
                    {
                        Key = $"shared|{transition.EntityPath}|{transition.Type}|"
                            + $"{transition.DestinationAreaId}|{transition.TileNeighborhoodSignature}",
                        EntityPath = transition.EntityPath,
                        Type = transition.Type,
                        DestinationAreaId = transition.DestinationAreaId,
                        DestinationAreaName = transition.DestinationAreaName,
                        TileNeighborhoodSignature = transition.TileNeighborhoodSignature,
                        TileNeighborhoodKeys = transition.TileNeighborhoodKeys.ToList(),
                        SuggestedRole = transition.Role,
                        ManualRole = transition.Role,
                    };
                    map.Transitions.Add(existing);
                }
                AddDistinct(existing.BossMonsterPaths, transition.BossMonsterPaths);
                AddDistinct(existing.TileNeighborhoodKeys, transition.TileNeighborhoodKeys);
            }
        }
    }

    internal static AtlasSharedKnowledgeDocument BuildCandidates(AtlasMapKnowledgeDocument source)
    {
        var result = new AtlasSharedKnowledgeDocument();
        foreach (var (areaId, map) in source.Maps.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var transitions = BuildTransitionCandidates(map);
            var assessment = AssessMap(map, transitions);
            result.Maps[areaId] = new AtlasSharedMapKnowledge
            {
                AreaId = map.AreaId,
                AreaName = map.AreaName,
                Confidence = assessment.Confidence,
                Promotable = assessment.Promotable || transitions.Any(item => item.Promotable),
                EvidenceInstances = assessment.EvidenceInstances,
                RequiredInstances = assessment.RequiredInstances,
                ConfidenceReason = assessment.Reason,
                HasSeparateBossArena = map.Transitions.Any(item =>
                        item.ManualRole.Equals("bossArena", StringComparison.OrdinalIgnoreCase))
                    || (map.ManualHasSeparateBossArena
                        ?? transitions.Any(item => item.Role.Equals("bossArena", StringComparison.OrdinalIgnoreCase)
                            && item.Promotable)),
                BossMonsterPaths = SelectBossPaths(map, transitions),
                BossNotes = map.ManualBossNotes,
                Transitions = transitions,
            };
        }
        return result;
    }

    internal static AtlasSharedKnowledgeDocument PromotableOnly(AtlasSharedKnowledgeDocument source)
    {
        var result = new AtlasSharedKnowledgeDocument();
        foreach (var (areaId, map) in source.Maps.Where(item => item.Value.Promotable))
        {
            result.Maps[areaId] = map with
            {
                Transitions = map.Transitions.Where(item => item.Promotable).ToList(),
            };
        }
        return result;
    }

    internal static void WriteCandidate(
        AtlasMapKnowledgeDocument source,
        string path,
        bool promotableOnly = false)
    {
        var document = BuildCandidates(source);
        if (promotableOnly) document = PromotableOnly(document);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, Json));
        File.Move(temporary, path, true);
    }

    private static List<AtlasSharedTransitionKnowledge> BuildTransitionCandidates(
        AtlasMapKnowledgeEntry map)
    {
        var semanticFamilyCount = map.Transitions
            .Where(item => item.TileNeighborhoodSignature.Length > 0)
            .Select(item => item.TileNeighborhoodSignature)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var groups = map.Transitions
            .Where(item => item.TileNeighborhoodSignature.Length > 0)
            .GroupBy(item => new TransitionFamily(
                item.EntityPath.ToLowerInvariant(), item.Type,
                item.DestinationAreaId.ToLowerInvariant(),
                item.TileNeighborhoodSignature.ToUpperInvariant()));
        var result = new List<AtlasSharedTransitionKnowledge>();
        foreach (var group in groups)
        {
            var samples = group.ToArray();
            var manualRole = samples.Select(item => item.ManualRole)
                .FirstOrDefault(role => !string.IsNullOrWhiteSpace(role));
            var bossPaths = samples.SelectMany(item => item.BossMonsterPaths)
                .Where(LooksLikeMapBoss)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var evidence = bossPaths.Count > 0
                ? samples.SelectMany(item => item.BossEvidenceInstanceHashes).Distinct().Count()
                : samples.SelectMany(item => item.TraversedInstanceHashes).Distinct().Count();
            var distinctive = samples.SelectMany(item => item.TileNeighborhoodKeys).Any(key =>
                key.Contains("boss", StringComparison.OrdinalIgnoreCase)
                || key.Contains("arena", StringComparison.OrdinalIgnoreCase));
            var required = !string.IsNullOrWhiteSpace(manualRole)
                ? 1
                : distinctive && bossPaths.Count == 1
                    ? 2
                    : semanticFamilyCount > 1 ? 5 : 3;
            var promotable = !string.IsNullOrWhiteSpace(manualRole) || evidence >= required;
            var role = !string.IsNullOrWhiteSpace(manualRole)
                ? manualRole
                : bossPaths.Count > 0 ? "bossArena" : "localTransition";
            var first = samples[0];
            result.Add(new AtlasSharedTransitionKnowledge
            {
                EntityPath = first.EntityPath,
                Type = first.Type,
                DestinationAreaId = first.DestinationAreaId,
                DestinationAreaName = first.DestinationAreaName,
                TileNeighborhoodSignature = first.TileNeighborhoodSignature,
                TileNeighborhoodKeys = samples.SelectMany(item => item.TileNeighborhoodKeys)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Role = role,
                BossMonsterPaths = bossPaths,
                Confidence = !string.IsNullOrWhiteSpace(manualRole)
                    ? 1 : Math.Min(0.99, evidence / (double)required),
                Promotable = promotable,
                EvidenceInstances = !string.IsNullOrWhiteSpace(manualRole) ? 1 : evidence,
                RequiredInstances = required,
                ConfidenceReason = !string.IsNullOrWhiteSpace(manualRole)
                    ? "human-confirmed role"
                    : distinctive ? "distinctive semantic boss/arena tile family" : "repeated transition family",
            });
        }
        return result.OrderBy(item => item.Role, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.TileNeighborhoodSignature, StringComparer.Ordinal)
            .ToList();
    }

    private static PromotionAssessment AssessMap(
        AtlasMapKnowledgeEntry map,
        IReadOnlyList<AtlasSharedTransitionKnowledge> transitions)
    {
        var manualTransition = map.Transitions.Any(item =>
            item.ManualRole.Equals("bossArena", StringComparison.OrdinalIgnoreCase)
            && item.BossMonsterPaths.Any(LooksLikeMapBoss));
        if ((map.ManualHasSeparateBossArena.HasValue || manualTransition)
            && (map.ManualBossMonsterPaths.Count > 0 || manualTransition))
            return new(1, true, 1, 1, "human-confirmed map boss and arena topology");

        var connected = map.UniqueMonsterEvidenceInstanceHashes
            .Where(item => LooksLikeMapBoss(item.Key))
            .Select(item => item.Value.Distinct().Count())
            .OrderDescending()
            .ToArray();
        var arena = transitions.Where(item => item.Role.Equals("bossArena", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Confidence)
            .FirstOrDefault();
        var connectedEvidence = connected.FirstOrDefault();
        if (arena is not null && arena.Confidence >= connectedEvidence / 3d)
            return new(arena.Confidence, arena.Promotable, arena.EvidenceInstances,
                arena.RequiredInstances, arena.ConfidenceReason);

        var ambiguous = connected.Length > 2;
        var required = ambiguous ? 5 : 3;
        return new(
            Math.Min(0.99, connectedEvidence / (double)required),
            connectedEvidence >= required,
            connectedEvidence,
            required,
            ambiguous ? "ambiguous boss candidates require five instances" : "repeated boss evidence");
    }

    private static List<string> SelectBossPaths(
        AtlasMapKnowledgeEntry map,
        IEnumerable<AtlasSharedTransitionKnowledge> transitions)
    {
        if (map.ManualBossMonsterPaths.Count > 0)
            return map.ManualBossMonsterPaths.Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase).ToList();
        return map.UniqueMonsterEvidenceInstanceHashes.Keys
            .Concat(transitions.SelectMany(item => item.BossMonsterPaths))
            .Where(LooksLikeMapBoss)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool LooksLikeMapBoss(string path)
    {
        var terminal = path[(path.LastIndexOf('/') + 1)..];
        var at = terminal.IndexOf('@');
        if (at >= 0) terminal = terminal[..at];
        return terminal.Contains("MapBoss", StringComparison.OrdinalIgnoreCase)
            || terminal.Contains("BossMap", StringComparison.OrdinalIgnoreCase)
            || terminal.StartsWith("Map", StringComparison.OrdinalIgnoreCase)
                && terminal.Contains("Boss", StringComparison.OrdinalIgnoreCase)
            || terminal.StartsWith("MapBanditLeader", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddDistinct(List<string> target, IEnumerable<string> additions)
    {
        foreach (var value in additions)
            if (!string.IsNullOrWhiteSpace(value)
                && !target.Contains(value, StringComparer.OrdinalIgnoreCase))
                target.Add(value);
    }

    private readonly record struct TransitionFamily(
        string EntityPath,
        AreaTransitionType? Type,
        string DestinationAreaId,
        string SemanticSignature);

    private readonly record struct PromotionAssessment(
        double Confidence,
        bool Promotable,
        int EvidenceInstances,
        int RequiredInstances,
        string Reason);
}

public sealed class AtlasSharedKnowledgeDocument
{
    public int SchemaVersion { get; set; } = 1;
    public Dictionary<string, AtlasSharedMapKnowledge> Maps { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed record AtlasSharedMapKnowledge
{
    public string AreaId { get; init; } = "";
    public string AreaName { get; init; } = "";
    public double Confidence { get; init; }
    public bool Promotable { get; init; }
    public int EvidenceInstances { get; init; }
    public int RequiredInstances { get; init; }
    public string ConfidenceReason { get; init; } = "";
    public bool HasSeparateBossArena { get; init; }
    public List<string> BossMonsterPaths { get; init; } = [];
    public string BossNotes { get; init; } = "";
    public List<AtlasSharedTransitionKnowledge> Transitions { get; init; } = [];
}

public sealed record AtlasSharedTransitionKnowledge
{
    public string EntityPath { get; init; } = "";
    public AreaTransitionType? Type { get; init; }
    public string DestinationAreaId { get; init; } = "";
    public string DestinationAreaName { get; init; } = "";
    public string TileNeighborhoodSignature { get; init; } = "";
    public List<string> TileNeighborhoodKeys { get; init; } = [];
    public string Role { get; init; } = "localTransition";
    public List<string> BossMonsterPaths { get; init; } = [];
    public double Confidence { get; init; }
    public bool Promotable { get; init; }
    public int EvidenceInstances { get; init; }
    public int RequiredInstances { get; init; }
    public string ConfidenceReason { get; init; } = "";
}
