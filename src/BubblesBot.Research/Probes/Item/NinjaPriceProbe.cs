using BubblesBot.Core;
using BubblesBot.Core.Knowledge;
using BubblesBot.Core.Snapshot;
using BubblesBot.Research.Probing;

namespace BubblesBot.Research.Probes.Item;

/// <summary>Durable smoke test for poe.ninja endpoint/schema drift and variant retention.</summary>
public sealed class NinjaPriceProbe : IProbe
{
    public string Name => "item.ninja-prices";
    public string Group => "item";
    public string Description => "Current poe.ninja API plus gem, cluster, and Voices variant rows.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx)
    {
        var snapshot = new GameSnapshot(ctx.Reader, ctx.Chain.IngameData, ctx.Chain.IngameState,
            new WindowInfo(0, 0, 1920, 1080));
        var league = string.IsNullOrWhiteSpace(snapshot.League) ? "Standard" : snapshot.League;
        var cache = Path.Combine(Path.GetTempPath(), $"bubbles-ninja-probe-{Guid.NewGuid():N}.json");
        try
        {
            var catalog = new PriceCatalog(league, TimeSpan.Zero, cache);
            catalog.RefreshAsync().GetAwaiter().GetResult();
            if (!string.IsNullOrEmpty(catalog.LastError))
                return ProbeResult.Fail($"league={league}: {catalog.LastError}");
            var voices = catalog.QuoteUnique("Voices");
            var gems = catalog.Variants("Fireball").Count(x => x.Category == "SkillGem");
            var clusters = catalog.Variants("12% increased Fire Damage").Count(x => x.Category == "ClusterJewel");
            if (catalog.EntryCount < 500 || catalog.VariantCount < 1_000 || !voices.IsKnown || gems == 0 || clusters == 0)
                return ProbeResult.Fail($"incomplete: league={league} entries={catalog.EntryCount} variants={catalog.VariantCount} voices={voices.MatchingVariants} fireball={gems} fireClusters={clusters}");
            return ProbeResult.Pass($"league={league} entries={catalog.EntryCount} variants={catalog.VariantCount}; "
                + $"Voices {voices.MinChaosValue:F1}-{voices.MaxChaosValue:F1}c ({voices.MatchingVariants} rows); "
                + $"Fireball rows={gems}; fire-cluster rows={clusters}");
        }
        finally
        {
            try { File.Delete(cache); } catch { }
        }
    }

    public ProbeResult Discover(ProbeContext ctx) => Validate(ctx);
}
