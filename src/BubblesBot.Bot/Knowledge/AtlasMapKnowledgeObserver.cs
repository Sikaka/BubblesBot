using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using BubblesBot.Bot.Diagnostics;
using BubblesBot.Core;
using BubblesBot.Core.Game;
using BubblesBot.Core.Pathfinding;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Knowledge;

/// <summary>
/// Passive atlas-map documentation. It runs while the human uses the overlay as well as during
/// automation, recording typed transitions, semantic tile neighborhoods, local terrain fingerprints,
/// and unique monsters observed after a transition. Observations are never trusted as automation
/// policy after one run: manual confirmation is immediate; automatic boss-arena promotion needs
/// corroboration from two distinct area instances.
/// </summary>
public sealed class AtlasMapKnowledgeObserver
{
    private const int MaxInstanceHistory = 16;
    private const int TransitionNearGrid = 70;
    private const int TransitionDisplacementGrid = 100;
    private const int PairedTransitionDisplacementGrid = 20;
    private static readonly long ObserveInterval = Stopwatch.Frequency / 2;
    private static readonly long BossAssociationWindow = Stopwatch.Frequency * 5 * 60;

    private readonly string _path;
    private readonly string _candidatePath;
    private readonly bool _includeSharedCatalog;
    private readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    private AtlasMapKnowledgeDocument _document;
    private long _nextObservationAt;
    private uint _previousAreaHash;
    private Vector2i? _previousPlayer;
    private string? _previousNearbyTransitionKey;
    private (string MapKey, string TransitionKey, uint InstanceHash, long ExpiresAt)? _pendingBossAssociation;

    public AtlasMapKnowledgeObserver(string? path = null)
    {
        _includeSharedCatalog = path is null;
        _path = path ?? AtlasKnowledgePromotion.DefaultObservationPath;
        _candidatePath = path is null
            ? AtlasKnowledgePromotion.DefaultCandidatePath
            : System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(path)!, "atlas-map-candidates.json");
        _document = Load();
    }

    public string Path => _path;
    public string CurrentMapAreaId { get; private set; } = "";
    public string CurrentMapName { get; private set; } = "";

    public IReadOnlyList<string> BossFragments(string mapName)
    {
        var map = FindMap(mapName);
        if (map is null) return [];
        var fragments = new HashSet<string>(map.ManualBossMonsterPaths, StringComparer.OrdinalIgnoreCase);
        foreach (var transition in map.Transitions)
            if (IsConfirmedBossArena(map, transition))
                fragments.UnionWith(transition.BossMonsterPaths);
        return fragments.ToArray();
    }

    public bool HasSeparateBossArena(string mapName)
    {
        var map = FindMap(mapName);
        return map is not null && (map.ManualHasSeparateBossArena == true
            || map.Transitions.Any(transition => IsConfirmedBossArena(map, transition)));
    }

    public void Observe(GameSnapshot snapshot, EntityCache entities)
    {
        var now = Stopwatch.GetTimestamp();
        if (now < _nextObservationAt) return;
        _nextObservationAt = now + ObserveInterval;
        if (_pendingBossAssociation is { } expiring && now >= expiring.ExpiresAt)
            _pendingBossAssociation = null;
        if (snapshot.Player is not { } player) return;
        var identity = ReadAreaIdentity(snapshot.Reader, snapshot.IngameDataAddress);
        if (identity.Id.Length == 0) return;
        if (!identity.Id.StartsWith("MapWorlds", StringComparison.OrdinalIgnoreCase))
        {
            CurrentMapAreaId = "";
            CurrentMapName = "";
            _previousAreaHash = snapshot.AreaHash;
            _previousPlayer = player.GridPosition;
            _previousNearbyTransitionKey = null;
            _pendingBossAssociation = null;
            return;
        }

        CurrentMapAreaId = identity.Id;
        CurrentMapName = identity.Name;

        var mapKey = identity.Id;
        var map = GetOrCreateMap(mapKey, identity.Name);
        var changed = AddCapped(map.ObservedInstanceHashes, snapshot.AreaHash);
        if (_document.SchemaVersion < 3)
        {
            _document.SchemaVersion = 3;
            changed = true;
        }
        map.LastObservedUtc = DateTime.UtcNow;

        var liveTransitions = entities.Entries.Values
            .Where(entry => !entry.IsStale && entry.Kind == EntityListReader.EntityKind.AreaTransition)
            .ToArray();
        string? nearestTransitionKey = null;
        var nearestTransitionDistance2 = long.MaxValue;
        foreach (var entry in liveTransitions)
        {
            var signature = TerrainLandmarkSignature.Compute(snapshot.Nav.PathReader, entry.GridPosition);
            var semantic = SemanticTileNeighborhood.Capture(snapshot.TileMap, entry.GridPosition);
            var key = TransitionKey(entry, signature);
            var observation = map.Transitions.FirstOrDefault(candidate => candidate.Key == key);
            if (observation is null)
            {
                observation = new AtlasTransitionKnowledge
                {
                    Key = key,
                    EntityPath = entry.Path,
                    Type = entry.AreaTransitionIdentityReadable ? entry.AreaTransitionType : null,
                    DestinationAreaId = entry.DestinationAreaId,
                    DestinationAreaName = entry.DestinationAreaName,
                    TerrainSignature = signature,
                    TileNeighborhoodSignature = semantic.Signature,
                    TileNeighborhoodKeys = semantic.Keys.ToList(),
                    TileNeighborhoodComponents = semantic.Components.ToList(),
                    SuggestedRole = entry.AreaTransitionType == AreaTransitionType.Local
                        ? "bossArenaCandidate"
                        : "zoneTransition",
                    FirstObservedUtc = DateTime.UtcNow,
                };
                map.Transitions.Add(observation);
                changed = true;
            }
            observation.LastObservedUtc = DateTime.UtcNow;
            changed |= AddCapped(observation.ObservedInstanceHashes, snapshot.AreaHash);
            if (observation.TileNeighborhoodSignature.Length == 0
                && semantic.Signature.Length > 0)
            {
                observation.TileNeighborhoodSignature = semantic.Signature;
                observation.TileNeighborhoodKeys = semantic.Keys.ToList();
                observation.TileNeighborhoodComponents = semantic.Components.ToList();
                changed = true;
            }
            changed |= AddDistinct(observation.TileMarkerPaths,
                snapshot.TileEntities.Entries
                    .Where(marker => marker.Path.Contains("AreaTransition", StringComparison.OrdinalIgnoreCase)
                        && DistanceSquared(marker.TileGridPosition, entry.GridPosition) <= 35L * 35L)
                    .Select(marker => marker.Path));

            var distance2 = DistanceSquared(player.GridPosition, entry.GridPosition);
            if (distance2 <= TransitionNearGrid * TransitionNearGrid
                && distance2 < nearestTransitionDistance2)
            {
                nearestTransitionDistance2 = distance2;
                nearestTransitionKey = key;
            }
        }

        var displaced = _previousPlayer is { } previous
            && DistanceSquared(previous, player.GridPosition)
                >= (long)TransitionDisplacementGrid * TransitionDisplacementGrid;
        var areaChanged = _previousAreaHash != 0 && _previousAreaHash != snapshot.AreaHash;
        var pairedTransitionSwitch = false;
        if (_previousPlayer is { } priorPosition
            && _previousNearbyTransitionKey is { } previousKey
            && nearestTransitionKey is { } currentKey
            && !previousKey.Equals(currentKey, StringComparison.Ordinal)
            && DistanceSquared(priorPosition, player.GridPosition)
                >= (long)PairedTransitionDisplacementGrid * PairedTransitionDisplacementGrid)
        {
            var previousTransition = map.Transitions.FirstOrDefault(candidate => candidate.Key == previousKey);
            var currentTransition = map.Transitions.FirstOrDefault(candidate => candidate.Key == currentKey);
            pairedTransitionSwitch = previousTransition is not null && currentTransition is not null
                && IsComplementaryLocalTransitionPair(map.AreaId, previousTransition, currentTransition);
        }
        if ((displaced || areaChanged || pairedTransitionSwitch)
            && _previousNearbyTransitionKey is { } traversedKey)
        {
            var priorMap = _document.Maps.Values.FirstOrDefault(candidate =>
                candidate.Transitions.Any(transition => transition.Key == traversedKey));
            var traversed = priorMap?.Transitions.FirstOrDefault(transition => transition.Key == traversedKey);
            if (priorMap is not null && traversed is not null)
            {
                changed |= AddCapped(traversed.TraversedInstanceHashes,
                    _previousAreaHash == 0 ? snapshot.AreaHash : _previousAreaHash);
                // Keep the association alive across a phased encounter. Palace, for example,
                // exposes PopeMapBoss first and DominusDemon (the loot-gating Husk) later; the
                // old one-shot association cleared after the first unique observation and lost
                // every subsequent phase. A later transition replaces this window immediately.
                _pendingBossAssociation = (
                    priorMap.AreaId, traversedKey, snapshot.AreaHash,
                    now + BossAssociationWindow);
                EventLog.Emit("map-knowledge", "map-knowledge.transition-traversed",
                    EventSeverity.Info,
                    $"observed transition traversal in '{priorMap.AreaName}' key={traversedKey}");
            }
        }

        var uniquePaths = entities.Entries.Values
            .Where(entry => !entry.IsStale
                && entry.Kind == EntityListReader.EntityKind.Monster
                && entry.Rarity == EntityListReader.EntityRarity.Unique
                && DistanceSquared(entry.GridPosition, player.GridPosition)
                    <= (long)GridConstants.NetworkBubbleGrid * GridConstants.NetworkBubbleGrid)
            .Select(entry => entry.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        changed |= AddDistinct(map.ObservedUniqueMonsterPaths, uniquePaths);
        foreach (var path in uniquePaths)
        {
            if (!map.UniqueMonsterEvidenceInstanceHashes.TryGetValue(path, out var instances))
            {
                instances = [];
                map.UniqueMonsterEvidenceInstanceHashes[path] = instances;
                changed = true;
            }
            changed |= AddCapped(instances, snapshot.AreaHash);
        }

        if (_pendingBossAssociation is { } pending && uniquePaths.Length > 0
            && _document.Maps.TryGetValue(pending.MapKey, out var parentMap))
        {
            var transition = parentMap.Transitions.FirstOrDefault(candidate => candidate.Key == pending.TransitionKey);
            if (transition is not null)
            {
                var pathsChanged = AddDistinct(transition.BossMonsterPaths, uniquePaths);
                var evidenceChanged = AddCapped(
                    transition.BossEvidenceInstanceHashes, pending.InstanceHash);
                var wasConfirmed = transition.AutoConfirmed;
                transition.SuggestedRole = "bossArenaCandidate";
                transition.AutoConfirmed = transition.BossEvidenceInstanceHashes.Count >= 2;
                if (transition.AutoConfirmed) parentMap.AutoHasSeparateBossArena = true;
                if (pathsChanged || evidenceChanged || transition.AutoConfirmed != wasConfirmed)
                {
                    EventLog.Emit("map-knowledge", "map-knowledge.boss-associated",
                        EventSeverity.Info,
                        $"associated {string.Join(", ", uniquePaths)} with '{parentMap.AreaName}' transition; "
                        + $"instances={transition.BossEvidenceInstanceHashes.Count}");
                    changed = true;
                }
            }
        }

        _previousAreaHash = snapshot.AreaHash;
        _previousPlayer = player.GridPosition;
        _previousNearbyTransitionKey = nearestTransitionKey;
        if (changed) Save();
    }

    private AtlasMapKnowledgeEntry GetOrCreateMap(string areaId, string areaName)
    {
        if (_document.Maps.TryGetValue(areaId, out var map))
        {
            if (!string.IsNullOrWhiteSpace(areaName)) map.AreaName = areaName;
            return map;
        }
        map = new AtlasMapKnowledgeEntry { AreaId = areaId, AreaName = areaName };
        _document.Maps[areaId] = map;
        return map;
    }

    private AtlasMapKnowledgeEntry? FindMap(string mapName)
        => _document.Maps.Values.FirstOrDefault(map =>
            map.AreaId.Equals(mapName, StringComparison.OrdinalIgnoreCase)
            || map.AreaName.Equals(mapName, StringComparison.OrdinalIgnoreCase));

    internal static bool IsConfirmedBossArena(AtlasMapKnowledgeEntry map, AtlasTransitionKnowledge transition)
        => transition.ManualRole.Equals("bossArena", StringComparison.OrdinalIgnoreCase)
            || transition.AutoConfirmed
            || map.ManualHasSeparateBossArena == true && transition.BossMonsterPaths.Count > 0;

    /// <summary>
    /// Same-area detached arenas such as Mesa expose two nearby local transitions: an entrance with
    /// no destination id and a return transition whose destination is the current map. Switching
    /// which member is nearest is traversal evidence even when the phase displacement is far below
    /// the ordinary 100-grid teleport threshold.
    /// </summary>
    internal static bool IsComplementaryLocalTransitionPair(
        string areaId,
        AtlasTransitionKnowledge first,
        AtlasTransitionKnowledge second)
    {
        if (first.Type != AreaTransitionType.Local || second.Type != AreaTransitionType.Local
            || !first.EntityPath.Equals(second.EntityPath, StringComparison.OrdinalIgnoreCase))
            return false;
        var firstReturns = first.DestinationAreaId.Equals(areaId, StringComparison.OrdinalIgnoreCase);
        var secondReturns = second.DestinationAreaId.Equals(areaId, StringComparison.OrdinalIgnoreCase);
        var firstUnknown = string.IsNullOrWhiteSpace(first.DestinationAreaId);
        var secondUnknown = string.IsNullOrWhiteSpace(second.DestinationAreaId);
        return firstReturns && secondUnknown || secondReturns && firstUnknown;
    }

    internal static string TransitionKey(
        string path,
        bool identityReadable,
        AreaTransitionType type,
        string destinationAreaId,
        string signature)
        => $"{path}|{(identityReadable ? (AreaTransitionType?)type : null)}|"
            + $"{destinationAreaId}|{signature}";

    private AtlasMapKnowledgeDocument Load()
    {
        try
        {
            return _includeSharedCatalog
                ? AtlasKnowledgePromotion.LoadMerged(_path)
                : AtlasKnowledgePromotion.LoadObservations(_path);
        }
        catch (Exception ex)
        {
            EventLog.Emit("map-knowledge", "map-knowledge.load-failed", EventSeverity.Warning,
                $"could not load '{_path}': {ex.Message}");
            return new AtlasMapKnowledgeDocument();
        }
    }

    private void Save()
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_document, _json));
            File.Move(temporary, _path, true);
            AtlasKnowledgePromotion.WriteCandidate(_document, _candidatePath);
        }
        catch (Exception ex)
        {
            EventLog.Emit("map-knowledge", "map-knowledge.save-failed", EventSeverity.Warning,
                $"could not save '{_path}': {ex.Message}");
        }
    }

    private static string TransitionKey(EntityCache.Entry entry, string signature)
        => TransitionKey(
            entry.Path,
            entry.AreaTransitionIdentityReadable,
            entry.AreaTransitionType,
            entry.DestinationAreaId,
            signature);

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

    private static bool AddCapped(List<uint> values, uint value)
    {
        if (value == 0 || values.Contains(value)) return false;
        values.Add(value);
        if (values.Count > MaxInstanceHistory) values.RemoveAt(0);
        return true;
    }

    private static bool AddDistinct(List<string> values, IEnumerable<string> additions)
    {
        var changed = false;
        foreach (var addition in additions)
        {
            if (string.IsNullOrWhiteSpace(addition)
                || values.Contains(addition, StringComparer.OrdinalIgnoreCase)) continue;
            values.Add(addition);
            changed = true;
        }
        return changed;
    }

    private static long DistanceSquared(Vector2i a, Vector2i b)
    {
        long dx = a.X - b.X;
        long dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }
}

public sealed class AtlasMapKnowledgeDocument
{
    public int SchemaVersion { get; set; } = 3;
    public Dictionary<string, AtlasMapKnowledgeEntry> Maps { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AtlasMapKnowledgeEntry
{
    public string AreaId { get; set; } = "";
    public string AreaName { get; set; } = "";
    public DateTime LastObservedUtc { get; set; }
    public List<uint> ObservedInstanceHashes { get; set; } = [];
    public List<string> ObservedUniqueMonsterPaths { get; set; } = [];
    public Dictionary<string, List<uint>> UniqueMonsterEvidenceInstanceHashes { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public List<AtlasTransitionKnowledge> Transitions { get; set; } = [];

    // Manual fields are deliberately first-class and preserved by the observer.
    public bool? ManualHasSeparateBossArena { get; set; }
    public List<string> ManualBossMonsterPaths { get; set; } = [];
    public string ManualBossNotes { get; set; } = "";
    public bool AutoHasSeparateBossArena { get; set; }
}

public sealed class AtlasTransitionKnowledge
{
    public string Key { get; set; } = "";
    public string EntityPath { get; set; } = "";
    public AreaTransitionType? Type { get; set; }
    public string DestinationAreaId { get; set; } = "";
    public string DestinationAreaName { get; set; } = "";
    public string TerrainSignature { get; set; } = "";
    public string TileNeighborhoodSignature { get; set; } = "";
    public List<string> TileNeighborhoodKeys { get; set; } = [];
    public List<string> TileNeighborhoodComponents { get; set; } = [];
    public List<string> TileMarkerPaths { get; set; } = [];
    public List<uint> ObservedInstanceHashes { get; set; } = [];
    public List<uint> TraversedInstanceHashes { get; set; } = [];
    public List<uint> BossEvidenceInstanceHashes { get; set; } = [];
    public List<string> BossMonsterPaths { get; set; } = [];
    public string SuggestedRole { get; set; } = "unknown";
    public string ManualRole { get; set; } = "";
    public bool AutoConfirmed { get; set; }
    public DateTime FirstObservedUtc { get; set; }
    public DateTime LastObservedUtc { get; set; }
}
