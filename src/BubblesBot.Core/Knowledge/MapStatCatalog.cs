namespace BubblesBot.Core.Knowledge;

/// <summary>
/// Per-stat knowledge for map items, keyed by the <c>Id</c> of the item's flattened stat
/// records (<c>ModsComponent.ItemStats</c> — see <c>KnownOffsets.ItemStatRecord</c>). This is
/// the substrate for "can my build run this map / how juiced is it": yield stats (quantity,
/// rarity, pack size) feed scoring, danger stats feed the per-build veto list.
///
/// <para><b>Ids are Stats.dat row indices and are LEAGUE/PATCH-VOLATILE.</b> Do not treat this
/// table as stable constants: after every patch, re-capture with
/// <c>BubblesBot.Research --probe item.mods --discover</c> (hover the item in-game for tooltip
/// ground truth; add <c>--poemcp</c> for mod RawNames) and update the entries. Unknown ids are
/// deliberately non-fatal — <see cref="Describe"/> falls back to an "unknown" record so a new
/// league's stats degrade to neutral instead of crashing a mode.</para>
///
/// <para>Initial capture 2026-07-14: T16 rare "Hypnotic Challenge" map
/// (Metadata/Items/Maps/MapKeyTier16, 8 mods, corrupted), all 13 records cross-checked against
/// the tooltip and POEMCP mod RawNames. Values are effective totals aggregated across mods;
/// "reduced X" stats store negative values; boolean stats store 1 (or 100 for %-chance-styled
/// booleans).</para>
///
/// <para>Per-build vetoes (the classic "cannot run": phys/ele reflect, no regen, no leech,
/// avoid-ailments for DoT builds) belong to the settings layer as a stat-id list — mirror of
/// <see cref="UltimatumModDanger"/>'s override mechanism. <see cref="StatInfo.SuggestVeto"/>
/// only marks stats that are veto candidates for SOME common build archetype, as a starting
/// point for that UI. Known hard-veto stats not yet captured this league (phys reflect,
/// no regen/leech) must be added here the first time a map rolls them.</para>
/// </summary>
public static class MapStatCatalog
{
    /// <summary>Which tooltip-header yield total a stat contributes to.</summary>
    public enum YieldKind { None, Quantity, Rarity, PackSize }

    public readonly record struct StatInfo(int Id, string Label, YieldKind Yield, bool SuggestVeto, string Note);

    /// <summary>Captured 2026-07-14 (see class remarks). Sorted by id for readability.</summary>
    public static readonly IReadOnlyDictionary<int, StatInfo> Known = new Dictionary<int, StatInfo>
    {
        [998]   = new(998,   "Monsters reflect #% of Elemental Damage",                   YieldKind.None,     true,  "mod MapMonsterElementalReflection*; hard veto for elemental-hit builds"),
        [1026]  = new(1026,  "+#% Monster pack size",                                     YieldKind.PackSize, false, "tooltip header total"),
        [1041]  = new(1041,  "#% increased Quantity of Items found",                      YieldKind.Quantity, false, "tooltip header total"),
        [1042]  = new(1042,  "UNKNOWN hidden stat (observed 28)",                         YieldKind.None,     false, "no tooltip line; adjacent to quantity id — recapture on other maps to pin down"),
        [1044]  = new(1044,  "UNKNOWN hidden stat (observed 18, same roll as reflect)",   YieldKind.None,     false, "possibly the paired/legacy elemental-reflect stat"),
        [1246]  = new(1246,  "#% increased Rarity of Items found",                        YieldKind.Rarity,   false, "tooltip header total"),
        [2349]  = new(2349,  "Monsters Poison on Hit",                                    YieldKind.None,     false, "boolean 1; mod MapPoisoningMapWorlds"),
        [5345]  = new(5345,  "Monsters have #% chance to Ignite, Freeze and Shock on Hit", YieldKind.None,    false, "mod MapMonstersChanceToInflictStatusAilments*"),
        [5350]  = new(5350,  "Monsters have #% chance to avoid Poison, Impale, Bleeding",  YieldKind.None,    true,  "mod MapMonstersAvoidPoisonBleedBlind*; veto candidate for poison/bleed builds"),
        [7246]  = new(7246,  "Monsters gain a Power Charge on Hit (#% chance)",           YieldKind.None,     false, "observed 100; mod MapMonstersPowerChargeOnHitMapWorlds"),
        [10934] = new(10934, "Area has patches of Chilled Ground (magnitude)",            YieldKind.None,     false, "observed 30; mod MapChilledGroundSuffix*"),
        [15640] = new(15640, "Monsters gain #% of Maximum Life as Extra Maximum Energy Shield", YieldKind.None, false, "mod MapMonstersMaximumLifeAddedEnergyShield*"),
        [15646] = new(15646, "Players have #% reduced effect of Non-Curse Auras from Skills",   YieldKind.None, true, "stored negative (-60 = 60% reduced); veto candidate for aura-stacking builds"),
    };

    /// <summary>Catalog entry, or a neutral "unknown" record for ids we haven't captured yet.</summary>
    public static StatInfo Describe(int id)
        => Known.TryGetValue(id, out var s)
            ? s
            : new StatInfo(id, $"unknown stat {id}", YieldKind.None, false,
                "not in catalog — capture via `--probe item.mods --discover` and add here");

    /// <summary>
    /// Aggregate verdict for one item's decoded stat list: yield totals plus which stats hit
    /// the veto list. <paramref name="buildVetoIds"/> is the per-build list sourced from
    /// settings; when null, the catalog's <see cref="StatInfo.SuggestVeto"/> defaults apply.
    /// </summary>
    public readonly record struct MapVerdict(int Quantity, int Rarity, int PackSize, IReadOnlyList<int> VetoHits)
    {
        public bool Runnable => VetoHits.Count == 0;
    }

    public static MapVerdict Evaluate(IEnumerable<(int Id, int Value)> stats, IReadOnlySet<int>? buildVetoIds = null)
    {
        int quantity = 0, rarity = 0, packSize = 0;
        var vetoes = new List<int>();
        foreach (var (id, value) in stats)
        {
            var info = Describe(id);
            switch (info.Yield)
            {
                case YieldKind.Quantity: quantity += value; break;
                case YieldKind.Rarity:   rarity   += value; break;
                case YieldKind.PackSize: packSize += value; break;
            }
            if (buildVetoIds is not null ? buildVetoIds.Contains(id) : info.SuggestVeto)
                vetoes.Add(id);
        }
        return new MapVerdict(quantity, rarity, packSize, vetoes);
    }
}
