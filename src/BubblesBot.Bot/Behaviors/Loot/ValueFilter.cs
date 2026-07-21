using BubblesBot.Bot.Settings;
using BubblesBot.Core.Game;
using BubblesBot.Core.Knowledge;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Behaviors.Loot;

/// <summary>
/// <see cref="ChaosValue"/> is the conservative value used by profit reporting;
/// <see cref="MaxChaosValue"/> is the highest plausible value used to avoid filtering an
/// unidentified shared-art unique that could be valuable.
/// </summary>
public readonly record struct LootEvaluation(
    bool ShouldTake,
    string Reason,
    float ChaosValue,
    string Category,
    float MaxChaosValue = 0f);

/// <summary>Memory-independent facts make loot decisions deterministic and unit-testable.</summary>
public sealed record LootItemFacts(
    string Name,
    string BaseName,
    string MetadataPath,
    EntityListReader.EntityRarity Rarity,
    bool Identified,
    int ItemLevel,
    int Quality,
    int GemLevel,
    bool Corrupted,
    int InventorySlots,
    string ResourcePath,
    string ClusterEnchantName = "",
    int ClusterPassiveCount = 0);

/// <summary>
/// Ordered loot policy: quest deny -> explicit overrides -> unique/art resolution -> exact
/// cluster/gem variants -> generic price. Unknown category data fails open and says why.
/// </summary>
public sealed class ValueFilter
{
    private static readonly string ClusterPrefix = "Metadata/Items/Jewels/JewelPassiveTreeExpansion";
    private readonly PriceCatalog _prices;

    public ValueFilter(PriceCatalog prices) { _prices = prices; }

    public LootEvaluation Evaluate(GroundLabelView label, LootSettings settings)
        => Evaluate(new LootItemFacts(
            label.ItemName,
            label.BaseName,
            label.InnerItemPath,
            label.ItemRarity,
            label.IsIdentified,
            label.ItemLevel,
            label.Quality,
            label.GemLevel,
            label.IsCorrupted,
            label.InventorySlots,
            label.ResourcePath,
            ClusterEnchantName: string.Empty,
            ClusterPassiveCount: label.ClusterPassiveCount), settings);

    public LootEvaluation Evaluate(LootItemFacts item, LootSettings settings)
    {
        var name = BestName(item);
        if (settings.IgnoreQuestItems && item.MetadataPath.Contains("/Quest", StringComparison.OrdinalIgnoreCase))
            return Skip("quest item", 0f, "quest");

        if (TryOverride(name, settings, out var overridden))
            return Take($"price override {overridden:F1}c", overridden, "override");

        if (Matches(name, settings.AlwaysLootItems, out var always))
            return Take($"always-loot match '{always}'", Price(name), "always-loot");

        if (item.Rarity == EntityListReader.EntityRarity.Unique)
            return EvaluateUnique(item, name, settings);
        if (IsClusterJewel(item.MetadataPath))
            return EvaluateCluster(item, name, settings);
        // Ritual offers can expose the gem metadata path before their SkillGem level is
        // readable. Do not fall through to the generic name lookup, which returns the most
        // expensive poe.ninja variant for that name (live: L0/Q10 Holy Sweep became 1,616c).
        // An unknown exact variant stays fail-open for ground loot but carries 0c, so it
        // cannot pass a Ritual shop value threshold.
        if (item.GemLevel > 0 || IsSkillGem(item.MetadataPath))
            return EvaluateGem(item, name, settings);
        if (settings.FilterSynthesisedItems && IsSynthesised(item.MetadataPath))
            return Skip("synthesised filtered", 0f, "synthesised");

        var value = Price(name);
        if (settings.MinChaosValue <= 0f) return Take("no floor configured", value, "generic");
        if (value >= settings.MinChaosValue)
            return Take($"value {value:F1}c >= {settings.MinChaosValue:F1}c", value, "generic");
        if (value <= 0f) return Take("unpriced item (no ninja data)", 0f, "generic");
        return Skip($"value {value:F1}c < {settings.MinChaosValue:F1}c", value, "generic");
    }

    private LootEvaluation EvaluateUnique(LootItemFacts item, string name, LootSettings settings)
    {
        var candidates = item.Identified
            ? new[] { name }
            : UniqueArtMapping.Shared.Resolve(item.ResourcePath).ToArray();

        // Must-loot is checked against resolved art candidates, so all three Simulacrum
        // cluster uniques are retained before identification.
        foreach (var candidate in candidates)
            if (Matches(candidate, settings.MustLootUniques, out var must))
            {
                var quote = UniqueQuote(candidate, item.ClusterPassiveCount);
                var value = OverrideOr(candidate, settings, quote.ChaosValue);
                return Take($"must-loot unique '{candidate}' ({must})", value, "must-loot",
                    OverrideOr(candidate, settings, quote.MaxChaosValue));
            }

        if (!settings.FilterUniques) return Take("uniques unfiltered", 0f, "unique");
        if (candidates.Length == 0)
            return Take($"unidentified unique (no art match for '{item.ResourcePath}')", 0f, "unique");

        var quotes = candidates.Select(c => (Name: c, Quote: UniqueQuote(c, item.ClusterPassiveCount))).ToArray();
        var known = quotes.Where(x => x.Quote.IsKnown).ToArray();
        if (known.Length == 0)
            return Take($"unique candidates unpriced: {string.Join(", ", candidates)}", 0f, "unique");

        var conservative = known.Min(x => OverrideOr(x.Name, settings, x.Quote.MinChaosValue));
        var plausibleMax = known.Max(x => OverrideOr(x.Name, settings, x.Quote.MaxChaosValue));
        if (plausibleMax >= settings.MinUniqueChaosValue)
            return Take($"unique plausible max {plausibleMax:F1}c >= {settings.MinUniqueChaosValue}c",
                conservative, "unique", plausibleMax);

        if (settings.MinChaosPerSlot > 0
            && plausibleMax / Math.Max(1, item.InventorySlots) >= settings.MinChaosPerSlot)
            return Take($"unique {plausibleMax / Math.Max(1, item.InventorySlots):F1}c/slot",
                conservative, "unique", plausibleMax);

        return Skip($"unique max {plausibleMax:F1}c below {settings.MinUniqueChaosValue}c",
            conservative, "unique", plausibleMax);
    }

    private LootEvaluation EvaluateCluster(LootItemFacts item, string name, LootSettings settings)
    {
        if (!settings.FilterClusterJewels) return Take("cluster jewels unfiltered", 0f, "cluster");
        if (string.IsNullOrWhiteSpace(item.ClusterEnchantName) || item.ClusterPassiveCount <= 0 || item.ItemLevel <= 0)
            return Take($"cluster details incomplete (enchant='{item.ClusterEnchantName}', passives={item.ClusterPassiveCount}, ilvl={item.ItemLevel})",
                0f, "cluster");

        var quote = _prices.QuoteCluster(item.ClusterEnchantName, item.ClusterPassiveCount, item.ItemLevel);
        var value = OverrideOr(name, settings, quote.ChaosValue);
        if (!quote.IsKnown) return Take($"unpriced cluster '{item.ClusterEnchantName}'", 0f, "cluster");
        if (value >= settings.MinClusterJewelChaosValue)
            return Take($"cluster {value:F1}c >= {settings.MinClusterJewelChaosValue}c", value, "cluster");
        return Skip($"cluster {value:F1}c < {settings.MinClusterJewelChaosValue}c", value, "cluster");
    }

    private LootEvaluation EvaluateGem(LootItemFacts item, string name, LootSettings settings)
    {
        var quote = _prices.QuoteGem(name, item.GemLevel, item.Quality, item.Corrupted);
        var value = OverrideOr(name, settings, quote.ChaosValue);
        if (settings.AlwaysLoot20QualityGems && item.Quality >= 20)
            return Take($"gem quality {item.Quality}% (>=20)", value, "gem", quote.MaxChaosValue);
        if (!settings.FilterSkillGems) return Take("gems unfiltered", value, "gem", quote.MaxChaosValue);
        if (!quote.IsKnown) return Take($"unpriced gem '{name}' L{item.GemLevel}/Q{item.Quality}", 0f, "gem");
        if (value >= settings.MinGemChaosValue)
            return Take($"gem {value:F1}c >= {settings.MinGemChaosValue}c", value, "gem", quote.MaxChaosValue);
        return Skip($"gem {value:F1}c < {settings.MinGemChaosValue}c", value, "gem", quote.MaxChaosValue);
    }

    private PriceQuote UniqueQuote(string name, int passiveCount)
        => _prices.QuoteUnique(name,
            name.Equals("Voices", StringComparison.OrdinalIgnoreCase) && passiveCount > 0
                ? passiveCount
                : null);

    private float Price(string name) => _prices.ValueChaos(name);

    private float OverrideOr(string name, LootSettings settings, float fallback)
        => TryOverride(name, settings, out var value) ? value : fallback;

    private static bool TryOverride(string name, LootSettings settings, out float value)
    {
        value = 0f;
        if (settings.PriceOverrides is null) return false;
        foreach (var row in settings.PriceOverrides)
        {
            if (string.IsNullOrWhiteSpace(row)) continue;
            var equals = row.LastIndexOf('=');
            if (equals <= 0 || equals >= row.Length - 1) continue;
            var configuredName = row[..equals].Trim();
            if (!name.Equals(configuredName, StringComparison.OrdinalIgnoreCase)) continue;
            if (float.TryParse(row[(equals + 1)..].Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value)
                && value >= 0f) return true;
        }
        value = 0f;
        return false;
    }

    private static bool Matches(string name, IReadOnlyList<string>? configured, out string match)
    {
        match = string.Empty;
        if (configured is null) return false;
        foreach (var row in configured)
        {
            if (string.IsNullOrWhiteSpace(row)) continue;
            var candidate = row.Trim();
            if (!name.Contains(candidate, StringComparison.OrdinalIgnoreCase)) continue;
            match = candidate;
            return true;
        }
        return false;
    }

    private static string BestName(LootItemFacts item)
        // Identified uniques show their unique name on the label; other item labels can
        // prepend stack counts ("4x Orb of Chance"), so Base.Info is the stable ninja key.
        => item.Rarity == EntityListReader.EntityRarity.Unique && item.Identified
            && !string.IsNullOrWhiteSpace(item.Name)
                ? item.Name.Trim()
                : !string.IsNullOrWhiteSpace(item.BaseName)
                    ? item.BaseName.Trim()
                    : !string.IsNullOrWhiteSpace(item.Name)
                        ? item.Name.Trim()
                        : ExtractPathName(item.MetadataPath);

    private static bool IsClusterJewel(string path)
        => path.StartsWith(ClusterPrefix, StringComparison.Ordinal);

    private static bool IsSkillGem(string path)
        => path.StartsWith("Metadata/Items/Gems/", StringComparison.OrdinalIgnoreCase);

    private static bool IsSynthesised(string path)
        => path.Contains("/Synthesised/", StringComparison.OrdinalIgnoreCase);

    private static string ExtractPathName(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        var slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    private static LootEvaluation Take(string reason, float value, string category, float max = 0f)
        => new(true, reason, value, category, max > 0f ? max : value);
    private static LootEvaluation Skip(string reason, float value, string category, float max = 0f)
        => new(false, reason, value, category, max > 0f ? max : value);
}
