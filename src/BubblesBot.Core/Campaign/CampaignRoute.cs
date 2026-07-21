using System.Text.Json;

namespace BubblesBot.Core.Campaign;

/// <summary>Thrown when the route JSON is malformed or contains a token type we do not model.</summary>
public sealed class CampaignRouteException(string message) : Exception(message);

/// <summary>
/// A contiguous run of the route spent inside one area: the area you are in, the in-area objectives
/// to complete there, prose/direction hints, and the transition token that leaves for
/// <see cref="NextAreaId"/>. The route is a flat ordered sequence; segmentation reconstructs the
/// per-area view guidance needs ("while in this zone, do X, then leave via the exit to Y").
/// </summary>
public sealed record RouteSegment(string AreaId)
{
    public List<RouteToken> Objectives { get; } = new();
    public List<RouteToken> Hints { get; } = new();

    /// <summary>The zone-changing token that leaves this area (null for the final segment).</summary>
    public RouteToken? ExitToken { get; internal set; }

    /// <summary>The area the exit leads to (null for the final segment).</summary>
    public string? NextAreaId { get; internal set; }
}

/// <summary>
/// Parsed exile-leveling campaign route: the acts as authored, plus a derived per-area
/// <see cref="Segments"/> view. Parse via <see cref="Parse"/>; unknown token types fail loudly
/// (per the campaign-runner D-02 contract — no silent skipping).
/// </summary>
public sealed class CampaignRoute
{
    public IReadOnlyList<RouteAct> Acts { get; }
    public IReadOnlyList<RouteSegment> Segments { get; }

    private readonly Dictionary<string, List<RouteSegment>> _byArea;

    private CampaignRoute(IReadOnlyList<RouteAct> acts, IReadOnlyList<RouteSegment> segments)
    {
        Acts = acts;
        Segments = segments;
        _byArea = new Dictionary<string, List<RouteSegment>>(StringComparer.OrdinalIgnoreCase);
        foreach (var seg in segments)
        {
            if (!_byArea.TryGetValue(seg.AreaId, out var list))
                _byArea[seg.AreaId] = list = new List<RouteSegment>(1);
            list.Add(seg);
        }
    }

    /// <summary>All route segments whose area id matches (a zone may be visited more than once).</summary>
    public IReadOnlyList<RouteSegment> SegmentsForArea(string areaId)
        => _byArea.TryGetValue(areaId, out var list) ? list : Array.Empty<RouteSegment>();

    /// <summary>Every area id the route visits — the validator set for <see cref="AreaIdentityReader"/>.</summary>
    public IReadOnlyCollection<string> AreaIds => _byArea.Keys;

    /// <summary>True if the route visits this area id (case-insensitive).</summary>
    public bool KnowsArea(string areaId) => _byArea.ContainsKey(areaId);

    /// <summary>Parse from the exile-leveling route JSON text (an array of acts).</summary>
    public static CampaignRoute Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new CampaignRouteException("route root must be a JSON array of acts");

        var acts = new List<RouteAct>();
        foreach (var actEl in doc.RootElement.EnumerateArray())
        {
            // The route array ends with a trailing metadata string (e.g. "pob-code:none"); skip
            // anything that is not an act object.
            if (actEl.ValueKind != JsonValueKind.Object) continue;
            var name = actEl.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var steps = new List<RouteStep>();
            if (actEl.TryGetProperty("steps", out var stepsEl) && stepsEl.ValueKind == JsonValueKind.Array)
                foreach (var stepEl in stepsEl.EnumerateArray())
                    steps.Add(ParseStep(stepEl));
            acts.Add(new RouteAct(name, steps));
        }

        return new CampaignRoute(acts, BuildSegments(acts));
    }

    private static RouteStep ParseStep(JsonElement stepEl)
    {
        // A step (or sub-step) may be a bare prose string rather than an object with parts/subSteps.
        if (stepEl.ValueKind == JsonValueKind.String)
            return new RouteStep(new[] { new RouteToken(RouteTokenType.Text, Text: stepEl.GetString()) }, Array.Empty<RouteStep>());
        if (stepEl.ValueKind != JsonValueKind.Object)
            return new RouteStep(Array.Empty<RouteToken>(), Array.Empty<RouteStep>());

        var parts = new List<RouteToken>();
        if (stepEl.TryGetProperty("parts", out var partsEl) && partsEl.ValueKind == JsonValueKind.Array)
            foreach (var partEl in partsEl.EnumerateArray())
                parts.Add(ParseToken(partEl));

        var subs = new List<RouteStep>();
        if (stepEl.TryGetProperty("subSteps", out var subsEl) && subsEl.ValueKind == JsonValueKind.Array)
            foreach (var subEl in subsEl.EnumerateArray())
                subs.Add(ParseStep(subEl));

        return new RouteStep(parts, subs);
    }

    private static RouteToken ParseToken(JsonElement el)
    {
        // A part is either a bare prose string or a typed token object.
        if (el.ValueKind == JsonValueKind.String)
            return new RouteToken(RouteTokenType.Text, Text: el.GetString());

        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty("type", out var typeEl))
            throw new CampaignRouteException($"route part is neither string nor typed object: {el.ValueKind}");

        var type = typeEl.GetString() ?? "";
        string? S(string prop) => el.TryGetProperty(prop, out var p) ? p.GetString() : null;
        int? I(string prop) => el.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : null;
        IReadOnlyList<string>? Arr(string prop) => el.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
            : null;

        return type switch
        {
            "enter"        => new RouteToken(RouteTokenType.Enter, AreaId: S("areaId")),
            "area"         => new RouteToken(RouteTokenType.Area, AreaId: S("areaId")),
            "kill"         => new RouteToken(RouteTokenType.Kill, Text: S("value")),
            "quest_text"   => new RouteToken(RouteTokenType.QuestText, Text: S("value")),
            "quest"        => new RouteToken(RouteTokenType.Quest, QuestId: S("questId"), RewardOffers: Arr("rewardOffers")),
            "waypoint_use" => new RouteToken(RouteTokenType.WaypointUse, DstAreaId: S("dstAreaId"), SrcAreaId: S("srcAreaId")),
            "waypoint_get" => new RouteToken(RouteTokenType.WaypointGet),
            "waypoint"     => new RouteToken(RouteTokenType.Waypoint),
            "dir"          => new RouteToken(RouteTokenType.Dir, DirIndex: I("dirIndex")),
            "arena"        => new RouteToken(RouteTokenType.Arena, Text: S("value")),
            "trial"        => new RouteToken(RouteTokenType.Trial),
            "crafting"     => new RouteToken(RouteTokenType.Crafting, CraftingRecipes: Arr("crafting_recipes")),
            "logout"       => new RouteToken(RouteTokenType.Logout, AreaId: S("areaId")),
            "generic"      => new RouteToken(RouteTokenType.Generic, Text: S("value")),
            "ascend"       => new RouteToken(RouteTokenType.Ascend, Version: S("version")),
            "portal_use"   => new RouteToken(RouteTokenType.PortalUse, DstAreaId: S("dstAreaId")),
            "portal_set"   => new RouteToken(RouteTokenType.PortalSet),
            _ => throw new CampaignRouteException($"unknown route token type '{type}'"),
        };
    }

    // ── Segmentation ────────────────────────────────────────────────────────

    private static readonly HashSet<RouteTokenType> ObjectiveKinds = new()
    {
        RouteTokenType.Kill, RouteTokenType.QuestText, RouteTokenType.Arena,
        RouteTokenType.WaypointGet, RouteTokenType.Quest, RouteTokenType.Trial,
        RouteTokenType.PortalSet, RouteTokenType.Generic,
    };

    private static List<RouteSegment> BuildSegments(IReadOnlyList<RouteAct> acts)
    {
        var segments = new List<RouteSegment>();
        RouteSegment? current = null;

        void Consume(RouteToken tok)
        {
            if (tok.ChangesArea)
            {
                var dest = tok.DestinationAreaId;
                if (current is not null)
                {
                    current.ExitToken = tok;
                    current.NextAreaId = dest;
                }
                if (!string.IsNullOrEmpty(dest))
                {
                    current = new RouteSegment(dest);
                    segments.Add(current);
                }
                return;
            }

            if (current is null) return;
            if (ObjectiveKinds.Contains(tok.Type)) current.Objectives.Add(tok);
            else current.Hints.Add(tok);
        }

        void Walk(RouteStep step)
        {
            foreach (var tok in step.Parts) Consume(tok);
            foreach (var sub in step.SubSteps) Walk(sub);
        }

        foreach (var act in acts)
            foreach (var step in act.Steps)
                Walk(step);

        return segments;
    }
}
