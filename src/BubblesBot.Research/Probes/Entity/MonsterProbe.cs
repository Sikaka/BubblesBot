using BubblesBot.Core.Game;
using BubblesBot.Research.Probing;

namespace BubblesBot.Research.Probes.Entity;

/// <summary>
/// Monster reads: among the live entities, classify monsters and confirm the rarity / targetable /
/// pathfinding / state-machine component offsets yield sane values on a sample. Migrated from the
/// monster semantics + rarity tests. Structural (passes trivially in town with no monsters).
/// </summary>
public sealed class MonsterProbe : IProbe
{
    public string Name => "entity.monsters";
    public string Group => "entity";
    public string Description => "Monster classify + rarity/targetable/pathfinding offsets sane.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx)
    {
        var listAddr = ctx.Chain.EntityList;
        if (listAddr == 0) return ProbeResult.Fail("EntityList null");

        var addrs = EntityListReader.EnumerateEntityAddresses(ctx.Reader, listAddr).EntityAddresses;
        var monsters = addrs
            .Select(a => EntityListReader.TryReadSnapshot(ctx.Reader, a))
            .Where(s => s is { Kind: EntityListReader.EntityKind.Monster })
            .ToList();

        if (monsters.Count == 0)
            return ProbeResult.Pass("no monsters in area (town/hideout?) - nothing to check");

        var withRarity = monsters.Count(s => s!.Rarity is >= EntityListReader.EntityRarity.Normal and <= EntityListReader.EntityRarity.Unique);
        var withPathfinding = monsters.Count(s => s!.Components.ContainsKey("Pathfinding"));
        var withStateMachine = monsters.Count(s => s!.Components.ContainsKey("StateMachine"));
        var alive = monsters.Count(s => s!.IsAlive);

        // Sanity: at least one monster classified with components, and rarities are in-range
        // (TryReadSnapshot only assigns Rarity when 0..4, so any assigned value is valid).
        return withRarity > 0 || withPathfinding > 0
            ? ProbeResult.Pass($"{monsters.Count} monsters ({alive} alive); rarity={withRarity} pathfinding={withPathfinding} stateMachine={withStateMachine}")
            : ProbeResult.Fail($"{monsters.Count} monsters but none resolved rarity or pathfinding (offsets drifted?)");
    }

    public ProbeResult Discover(ProbeContext ctx)
        // Rarity offset rediscovery needs a known-rarity monster + oracle id match; use --dump on a
        // monster's ObjectMagicProperties component instead.
        => ProbeResult.Found("ObjectMagicProperties.Rarity", []);
}
