namespace BubblesBot.Bot.Modes;

public readonly record struct RitualShopCandidate(int Index, float TotalChaos, bool IsLiquidFiller);

/// <summary>Pure offer selection for deterministic testing and replay.</summary>
public static class RitualShopPolicy
{
    /// <summary>
    /// Low-value final-spend fallback. Keep this deliberately narrow: generic crafting
    /// currency and scarabs are liquid in bulk; essences, oils, omens, cards, maps, and
    /// equipment must clear the ordinary value threshold instead.
    /// </summary>
    public static bool IsLiquidFillerPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.Contains("/Scarabs/", StringComparison.OrdinalIgnoreCase)) return true;
        if (!path.StartsWith("Metadata/Items/Currency/", StringComparison.OrdinalIgnoreCase))
            return false;

        var leaf = path[(path.LastIndexOf('/') + 1)..];
        return leaf.StartsWith("Currency", StringComparison.OrdinalIgnoreCase)
            && !leaf.StartsWith("CurrencyEssence", StringComparison.OrdinalIgnoreCase)
            && !path.Contains("/Essence", StringComparison.OrdinalIgnoreCase)
            && !path.Contains("/Delve", StringComparison.OrdinalIgnoreCase)
            && !path.Contains("/Blight", StringComparison.OrdinalIgnoreCase)
            && !path.Contains("/Ancestral", StringComparison.OrdinalIgnoreCase)
            && !leaf.Contains("Ritual", StringComparison.OrdinalIgnoreCase);
    }

    public static int? SelectIndex(
        IReadOnlyList<RitualShopCandidate> candidates,
        bool canReroll,
        float rerollThresholdChaos,
        float finalBuyMinimumChaos)
    {
        var valuableFloor = canReroll ? rerollThresholdChaos : finalBuyMinimumChaos;
        var valuable = candidates
            .Where(x => x.TotalChaos >= valuableFloor)
            .OrderByDescending(x => x.TotalChaos)
            .ToArray();
        if (valuable.Length > 0) return valuable[0].Index;

        if (canReroll) return null;
        var liquid = candidates
            .Where(x => x.IsLiquidFiller)
            .OrderByDescending(x => x.TotalChaos)
            .ToArray();
        return liquid.Length > 0 ? liquid[0].Index : null;
    }
}
