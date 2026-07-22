namespace BubblesBot.Bot.Strategies;

public enum GuardianWitnessStatus
{
    Unknown,
    NeedsWitness,
    Witnessed,
}

public readonly record struct GuardianAtlasState(
    string MapName,
    GuardianWitnessStatus WitnessStatus,
    IReadOnlyList<string> TooltipLines);

/// <summary>
/// Stable facts and fail-closed selection for the Shaper Guardian / Maven "The Formed" rotation.
/// Atlas state is derived only from the tooltip belonging to the currently proven map-node hover.
/// </summary>
public static class GuardianRotationPolicy
{
    public const string WitnessedTooltipMarker =
        "The Maven currently holds a re-creation of this map's Boss.";

    public static readonly IReadOnlyList<string> Maps =
    [
        "Forge of the Phoenix",
        "Maze of the Minotaur",
        "Pit of the Chimera",
        "Lair of the Hydra",
    ];

    public static GuardianAtlasState ClassifyTooltip(IEnumerable<string> tooltipLines)
    {
        var lines = tooltipLines
            .SelectMany(x => x.Split(['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();
        var mapName = Maps.FirstOrDefault(map => lines.Any(line =>
            line.Equals(map, StringComparison.OrdinalIgnoreCase)));
        if (mapName is null)
            return new GuardianAtlasState(string.Empty, GuardianWitnessStatus.Unknown, lines);

        var witnessed = lines.Any(line =>
            line.Contains("The Maven currently holds a re-creation of this map's Boss",
                StringComparison.OrdinalIgnoreCase));
        return new GuardianAtlasState(
            mapName,
            witnessed ? GuardianWitnessStatus.Witnessed : GuardianWitnessStatus.NeedsWitness,
            lines);
    }

    /// <summary>
    /// Select the first unwitnessed guardian in rotation order. Returns false when any member is
    /// missing/unknown; invitation readiness is granted only when all four are positively witnessed.
    /// </summary>
    public static bool TrySelectNext(
        IReadOnlyDictionary<string, GuardianWitnessStatus> states,
        out string? nextMap,
        out bool invitationReady)
    {
        nextMap = null;
        invitationReady = false;
        foreach (var map in Maps)
        {
            if (!states.TryGetValue(map, out var status) || status == GuardianWitnessStatus.Unknown)
                return false;
            if (status == GuardianWitnessStatus.NeedsWitness && nextMap is null)
                nextMap = map;
        }
        invitationReady = nextMap is null;
        return true;
    }
}
