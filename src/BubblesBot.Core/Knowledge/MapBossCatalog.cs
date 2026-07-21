namespace BubblesBot.Core.Knowledge;

/// <summary>
/// Built-in map → expected map-boss identities. These are per-patch game facts validated by us,
/// NOT user policy: a strategy only asserts <c>requireBossKill</c>, and the catalog decides which
/// monster metadata fragments constitute "the boss(es) of this map." Keeping them here (not in
/// shareable strategy files) prevents an imported strategy from asserting false completion
/// evidence.
///
/// <para>Boss-kill completion is gated in the current build; this catalog is the data foundation
/// that <c>BossEvidenceTracker</c> consumes once the runtime enables the boss-hunt phase.</para>
/// </summary>
public static class MapBossCatalog
{
    /// <summary>
    /// Map display name → the metadata-path fragments of its unique boss(es). A map with multiple
    /// bosses (e.g. City Square) lists all of them; completion requires death evidence for each.
    /// Fragments are matched case-insensitively as substrings of an entity's metadata path.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> Bosses =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            // City Square has multiple unique boss entities — "one unique disappeared" is not
            // sufficient completion evidence, which is why an explicit roster is required.
            ["City Square"] =
            [
                "Metadata/Monsters/AtlasExiles/AtlasExile",   // placeholder roster; validate live per patch
            ],
            ["Strand"] =
            [
                // Standard post-migration live oracle, 2026-07-20: HeavyStrike is visible
                // first, then MapBanditLeaderKraityn streams/spawns after the fight begins.
                // Both are real unique monsters and both must be dead before leaving the arena.
                "Metadata/Monsters/Bandits/MapBanditBossHeavyStrike",
                "Metadata/Monsters/BanditLeaderDexInt/MapBanditLeaderKraityn",
            ],
        };

    // Maps whose required boss is reached through an in-map area transition. This is separate
    // from the boss roster because it controls traversal, not kill evidence.
    private static readonly HashSet<string> SeparateBossArenas =
        new(StringComparer.OrdinalIgnoreCase) { "Strand" };

    // Maps whose required boss arena is the terminal traversal objective. Once the exact
    // roster is dead, remaining reveal is disconnected-arena accounting or backward travel,
    // not useful map progress.
    private static readonly HashSet<string> TerminalBossMaps =
        new(StringComparer.OrdinalIgnoreCase) { "Strand" };

    public static bool HasEntry(string mapName) => Bosses.ContainsKey(mapName.Trim());

    /// <summary>Expected boss metadata fragments for the map, or empty if the map has no catalog entry.</summary>
    public static IReadOnlyList<string> BossFragments(string mapName)
        => Bosses.TryGetValue(mapName.Trim(), out var fragments) ? fragments : [];

    public static bool HasSeparateBossArena(string mapName)
        => SeparateBossArenas.Contains(mapName.Trim());

    public static bool BossCompletesTraversal(string mapName)
        => TerminalBossMaps.Contains(mapName.Trim());

    public static IReadOnlyCollection<string> KnownMaps => Bosses.Keys.ToArray();
}
