using System.Text.Json;
using System.Text.Json.Serialization;

namespace BubblesBot.Core.Campaign;

/// <summary>How a <see cref="TargetDescription"/> is located in the world.</summary>
public enum TargetKind
{
    /// <summary>Matched against terrain tile detail-names / <c>.tdt</c> paths (the common case).</summary>
    Tile,
    /// <summary>Matched against a live entity's metadata path.</summary>
    Entity,
}

/// <summary>
/// One point-of-interest description, mirroring an entry in the Radar plugin's <c>targets.json</c>.
/// <see cref="Name"/> is a tile detail-name token, a full <c>.tdt</c> path, or an entity path;
/// <see cref="ExpectedCount"/> is the K used when clustering many matching cells into representative
/// markers (Phase B).
/// </summary>
public sealed record TargetDescription
{
    [JsonPropertyName("Name")] public string Name { get; init; } = "";
    [JsonPropertyName("DisplayName")] public string? DisplayName { get; init; }
    [JsonPropertyName("ExpectedCount")] public int ExpectedCount { get; init; } = 1;
    [JsonPropertyName("TargetType")] public TargetKind TargetType { get; init; } = TargetKind.Tile;
    [JsonPropertyName("Color")] public string? Color { get; init; }

    /// <summary>Friendly label, falling back to <see cref="Name"/> when no display name is set.</summary>
    public string Label => string.IsNullOrEmpty(DisplayName) ? Name : DisplayName!;
}

/// <summary>
/// The curated point-of-interest catalog: area-name glob → target descriptions. Loaded once from the
/// <c>targets.json</c> schema. <see cref="ForArea"/> aggregates every entry whose key matches a live
/// area's <c>RawName</c> (so <c>"*"</c> applies everywhere and family globs like <c>"*Heist*"</c>
/// apply to a zone family) — the Radar <c>GetTargetDescriptionsInArea</c> semantics.
/// </summary>
public sealed class CampaignTargets
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IReadOnlyDictionary<string, IReadOnlyList<TargetDescription>> _byAreaKey;

    public IReadOnlyCollection<string> AreaKeys => (IReadOnlyCollection<string>)_byAreaKey.Keys;

    private CampaignTargets(IReadOnlyDictionary<string, IReadOnlyList<TargetDescription>> byAreaKey)
        => _byAreaKey = byAreaKey;

    public static CampaignTargets Parse(string json)
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, List<TargetDescription>>>(json, JsonOpts)
                  ?? new Dictionary<string, List<TargetDescription>>();
        var map = new Dictionary<string, IReadOnlyList<TargetDescription>>(StringComparer.Ordinal);
        foreach (var (k, v) in raw) map[k] = v;
        return new CampaignTargets(map);
    }

    /// <summary>Empty catalog — used before the file is loaded.</summary>
    public static readonly CampaignTargets Empty =
        new(new Dictionary<string, IReadOnlyList<TargetDescription>>());

    /// <summary>Every target whose area-key glob matches <paramref name="rawName"/>.</summary>
    public IReadOnlyList<TargetDescription> ForArea(string rawName)
    {
        var result = new List<TargetDescription>();
        foreach (var (key, targets) in _byAreaKey)
            if (GlobPattern.Like(rawName, key))
                result.AddRange(targets);
        return result;
    }

    /// <summary>Targets from keys that match <paramref name="rawName"/> but are NOT the global
    /// <c>"*"</c> fallback — i.e. the curated, zone-specific points of interest (transitions, quest
    /// objectives like Rhoa nests, bosses) without the league-mechanic entries that apply
    /// everywhere.</summary>
    public IReadOnlyList<TargetDescription> ForAreaSpecific(string rawName)
    {
        var result = new List<TargetDescription>();
        foreach (var (key, targets) in _byAreaKey)
            if (key != "*" && GlobPattern.Like(rawName, key))
                result.AddRange(targets);
        return result;
    }

    /// <summary>True if any area key matches — i.e. the catalog has coverage for this zone.</summary>
    public bool Covers(string rawName)
    {
        foreach (var key in _byAreaKey.Keys)
            if (GlobPattern.Like(rawName, key))
                return true;
        return false;
    }
}
