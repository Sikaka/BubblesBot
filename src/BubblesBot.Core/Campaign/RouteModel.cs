namespace BubblesBot.Core.Campaign;

/// <summary>
/// The kind of a single route token, mirroring the exile-leveling route JSON <c>type</c> field.
/// <see cref="Text"/> is the synthetic kind for a bare prose string inside a step's <c>parts</c>
/// array (the route interleaves plain strings with typed token objects).
/// </summary>
public enum RouteTokenType
{
    Text,
    Enter,
    Kill,
    QuestText,
    Quest,
    WaypointUse,
    WaypointGet,
    Waypoint,
    Dir,
    Arena,
    Trial,
    Crafting,
    Logout,
    Generic,
    Ascend,
    Area,
    PortalUse,
    PortalSet,
}

/// <summary>
/// One token from a route step's <c>parts</c> (or a subStep). A discriminated payload — only the
/// fields relevant to <see cref="Type"/> are populated. Kept as a single record rather than a class
/// hierarchy so the parser and consumers stay allocation-light and easy to pattern-match.
/// </summary>
public sealed record RouteToken(
    RouteTokenType Type,
    string? Text = null,
    string? AreaId = null,
    string? SrcAreaId = null,
    string? DstAreaId = null,
    string? QuestId = null,
    IReadOnlyList<string>? RewardOffers = null,
    IReadOnlyList<string>? CraftingRecipes = null,
    int? DirIndex = null,
    string? Version = null)
{
    /// <summary>Zone-changing tokens: entering these advances the "current area" context.</summary>
    public bool ChangesArea =>
        Type is RouteTokenType.Enter or RouteTokenType.Area or RouteTokenType.WaypointUse or RouteTokenType.PortalUse;

    /// <summary>The area this token moves the player into, if it is a zone-changer.</summary>
    public string? DestinationAreaId => Type switch
    {
        RouteTokenType.Enter => AreaId,
        RouteTokenType.Area => AreaId,
        RouteTokenType.WaypointUse => DstAreaId,
        RouteTokenType.PortalUse => DstAreaId,
        _ => null,
    };
}

/// <summary>A single route step: an ordered list of tokens plus any nested clarifying sub-steps.</summary>
public sealed record RouteStep(IReadOnlyList<RouteToken> Parts, IReadOnlyList<RouteStep> SubSteps);

/// <summary>One act of the campaign route (e.g. "Act 1") and its ordered steps.</summary>
public sealed record RouteAct(string Name, IReadOnlyList<RouteStep> Steps);
