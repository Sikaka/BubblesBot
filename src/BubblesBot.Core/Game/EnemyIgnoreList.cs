namespace BubblesBot.Core.Game;

/// <summary>
/// Names of monsters whose identity matches "real boss / rare" filters but which the bot
/// should treat as trash:
/// <list type="bullet">
///   <item><b>Volatile</b> — monster mod summons. Show as Unique rarity, no real
///         targetability, explode on contact. We don't want a nameplate cluttering the
///         screen for every Volatile in a pack and we don't want combat to pick them as
///         priority targets.</item>
///   <item><b>Tormented Spirits</b> — fly through monsters and grant buffs when killed,
///         not really an attack target.</item>
/// </list>
///
/// <para><b>Mutable.</b> Add names as you encounter them. The runtime check is a single
/// hash-set lookup keyed on <c>Entity.RenderName</c>, so the cost is constant.</para>
///
/// <para><b>Why name-based, not path-based.</b> Many of these share metadata paths with
/// real monsters (Volatiles inherit from a normal mob template); name is what visually
/// identifies them. PoE's display name is stable across sessions.</para>
/// </summary>
public static class EnemyIgnoreList
{
    private static readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase)
    {
        "Volatile",
        "Tormented Spirit",
        "Sister Cassia",     // friendly NPC who runs the blight encounter — shows as unique
        "Living Crystal",    // map-decoration spawner; carries Unique rarity tag but is not a fight target
    };

    /// <summary>True iff the monster's RenderName matches an ignored entry (case-insensitive).</summary>
    public static bool IsIgnored(string? name)
        => !string.IsNullOrEmpty(name) && _names.Contains(name);

    /// <summary>Add a name to the ignore list at runtime. Future ticks will filter it out.</summary>
    public static void Add(string name)
    {
        if (!string.IsNullOrEmpty(name)) _names.Add(name);
    }

    public static IReadOnlyCollection<string> All => _names;
}
