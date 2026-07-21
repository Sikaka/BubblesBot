using BubblesBot.Core.Game;
using BubblesBot.Research.Probing;

namespace BubblesBot.Research.Probes.Entity;

/// <summary>
/// Walks the EntityList tree and asserts it is structurally healthy: a plausible entity count, the
/// local player present among them, and a sample entity resolving a non-empty component map. Proves
/// EntityList.Root traversal + Entity.Id/ComponentList offsets together. Structural — no baseline.
/// </summary>
public sealed class EntityListProbe : IProbe
{
    public string Name => "entity.list";
    public string Group => "entity";
    public string Description => "EntityList traverses; player present; sample entity has components.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx)
    {
        var listAddr = ctx.Chain.EntityList;
        if (listAddr == 0) return ProbeResult.Fail("EntityList pointer null");

        var result = EntityListReader.EnumerateEntityAddresses(ctx.Reader, listAddr);
        var addrs = result.EntityAddresses;
        if (addrs.Count is <= 0 or > 100_000)
            return ProbeResult.Fail($"entity count implausible ({addrs.Count}); badReads={result.BadReads}");

        var player = ctx.Chain.Player;
        var playerPresent = player != 0 && addrs.Contains(player);

        var withComponents = addrs.Take(64).Count(a => EntityComponents.ReadComponentMap(ctx.Reader, a).Count > 0);
        if (withComponents == 0)
            return ProbeResult.Fail($"{addrs.Count} entities but none resolved a component map (Entity.ComponentList drifted?)");

        var msg = $"{addrs.Count} entities, player {(playerPresent ? "present" : "NOT FOUND")}, " +
                  $"{withComponents}/{Math.Min(64, addrs.Count)} sampled have components";
        // Player missing from its own entity list is suspicious but not always fatal (timing); flag it.
        return playerPresent ? ProbeResult.Pass(msg) : ProbeResult.Fail(msg);
    }

    public ProbeResult Discover(ProbeContext ctx)
        // Traversal offsets are exercised structurally by Validate; no value-scan target.
        => ProbeResult.Found("EntityList.Root", []);
}
