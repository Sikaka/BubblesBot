namespace BubblesBot.Core.Knowledge;

/// <summary>
/// Ultimatum modifier-danger weighting table. Each mod has a numeric danger score; the
/// encounter loop picks the lowest-danger option per round (greedy) and gives up the
/// encounter when cumulative danger crosses a threshold or all options are blocked.
///
/// <para><b>Scale.</b> 0 = Free, 1 = Easy, 3 = Medium (default for unknown), 5 = Hard,
/// 10 = Very Hard, <see cref="BlockedValue"/> (999) = never pick.</para>
///
/// <para>Defaults below are AutoExile's softcore-mapping baseline. Per-user overrides
/// plug in via the <c>userOverrides</c> argument on <see cref="GetDanger"/>; the bot's
/// settings layer will source those from a string-list ("ModId=value" pairs).</para>
///
/// <para><b>Picking logic.</b> Use <see cref="PickBestModifier"/> over a list of available
/// mod ids — returns the index of the lowest-danger pickable mod, or -1 if everything's
/// blocked. The encounter loop "abandons" in the -1 case.</para>
/// </summary>
public static class UltimatumModDanger
{
    public const int BlockedValue  = 999;
    public const int DefaultDanger = 3;   // unknown mods → Medium

    /// <summary>One row in the mod-table UI — id is the stable PoE identifier, display
    /// name is the localized in-game text (or our best english label for it).</summary>
    public readonly record struct ModInfo(string Id, string DisplayName, int DefaultDanger);

    /// <summary>Catalog of every modifier the bot knows about, in roughly the order users
    /// will recognize them (lowest danger first, hard skips last). The web UI table renders
    /// from this list; users can override per-mod, and overrides are stored as <c>"id=N"</c>
    /// strings in <see cref="UltimatumSettings.ModDangerOverrides"/>.</summary>
    public static readonly IReadOnlyList<ModInfo> KnownMods = new ModInfo[]
    {
        // ── Free / Easy ─────────────────────────────────────────────────
        new("MonsterBuffAcceleratingSpeed",    "Escalating Monster Speed",  0),
        new("MonsterBuffAdditionalProjectiles","Additional Projectiles",    0),
        new("Radius1",                         "Limited Arena",            -1),
        new("MonsterBuffChaosDamage",          "Chaos Damage",              1),
        new("MonsterBuffIncreasedDamage",      "Increased Damage",          1),
        new("MonsterBuffResistances",          "Monster Resistances",       1),
        new("MonsterBuffLife",                 "Increased Life",            1),
        new("PlayerDebuffReducedDamage",       "Reduced Damage",            1),
        new("MonsterHitsAreCriticalStrikes",   "Deadly Monsters",           1),
        new("FlamespitterDaemon1",             "Raging Dead I",             1),
        new("ChaosCloudDaemon1",               "Choking Miasma I",          1),
        // ── Medium ──────────────────────────────────────────────────────
        new("FlamespitterDaemon2",             "Raging Dead II",            3),
        new("FlamespitterDaemon3",             "Raging Dead III",           3),
        new("ChaosCloudDaemon2",               "Choking Miasma II",         3),
        new("ChaosCloudDaemon3",               "Choking Miasma III",        3),
        new("PlayerDebuffReducedRecovery",     "Reduced Recovery",          3),
        new("PlayerDebuffIncreasingVulnerability", "Escalating Damage Taken", 5),
        // ── Hard ────────────────────────────────────────────────────────
        new("PlayerDebuffNoLeech",             "No Leech",                  5),
        new("PlayerDebuffNoRegen",             "No Regeneration",           5),
        // ── Never-take ──────────────────────────────────────────────────
        new("RevenantDaemon1",                 "Stalking Ruin I",         999),
        new("RevenantDaemon2",                 "Stalking Ruin II",        999),
        new("RevenantDaemon3",                 "Stalking Ruin III",       999),
        new("RevenantDaemon4",                 "Stalking Ruin IV",        999),
        new("MonstersApplyRuin1",              "Ruin I",                  999),
        new("MonstersApplyRuin2",              "Ruin II",                 999),
        new("MonstersApplyRuin3",              "Ruin III",                999),
        new("MonstersApplyRuin4",              "Ruin IV",                 999),
        new("AltarDaemon1",                    "Blood Altar I",           999),
        new("AltarDaemon2",                    "Blood Altar II",          999),
        new("AltarDaemon3",                    "Blood Altar III",         999),
        new("PlayerDebuffLimitedFlasks",       "Limited Flasks",          999),
        new("PlayerDebuffHinderedMsOnFlaskUse","Hindering Flasks",        999),
        new("PlayerDebuffNullification",       "Nullification",           999),
    };

    /// <summary>Named danger tiers for the UI dropdown — value pairs with
    /// <see cref="GetDanger"/>. Users pick a label; the bot stores the int.</summary>
    public static readonly (string Label, int Value)[] Tiers =
    {
        ("Free",      0),
        ("Easy",      1),
        ("Medium",    3),
        ("Hard",      5),
        ("Very Hard", 10),
        ("NEVER",     999),
    };

    /// <summary>
    /// Default danger ratings tuned for a cast-on-stunned chieftain build (flask-reliant +
    /// charge / buff-stacking sustain). Any mod that disrupts the build's core loop — Ruin
    /// daemons, anti-flask debuffs, charge nullification — is marked NEVER-TAKE (999).
    ///
    /// <para><b>Tuning tiers:</b></para>
    /// <list type="bullet">
    ///   <item><b>Beneficial (-1)</b> — actively help the run (more loot, faster waves).
    ///         Always preferred when offered.</item>
    ///   <item><b>Free (0)</b> — barely noticeable. Always safe to take.</item>
    ///   <item><b>Easy (1)</b> — manageable; minor friction.</item>
    ///   <item><b>Medium (3)</b> — default for unknown mods; noticeable danger.</item>
    ///   <item><b>Hard (5)</b> — build-dependent; pushes the comfort zone.</item>
    ///   <item><b>Very hard (10)</b> — bots dodge poorly here.</item>
    ///   <item><b>Skip (999)</b> — never pick; if all 3 options are skip, abandon the run.</item>
    /// </list>
    ///
    /// <para>Build-specific overrides go through the <c>userOverrides</c> argument on
    /// <see cref="GetDanger"/> — sourced from the <c>ModDangerOverrides</c> settings list.</para>
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int> Defaults = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        // ── Beneficial (-1) — picking these makes the run better, not worse ──
        // (None confirmed yet — PoE's Ultimatum mods are all monster-buff or player-debuff.
        //  Placeholder slot in case GGG adds "+drops" / "+wave speed" style modifiers.)

        // ── Free (0) — barely noticeable for this build ──────────────
        ["MonsterBuffAcceleratingSpeed"]    = 0,   // Escalating Monster Speed
        ["MonsterBuffAdditionalProjectiles"]= 0,   // Additional Projectiles
        ["Radius1"]                         = -1,  // Limited Arena (halves encounter area — anchor build LIKES this)

        // ── Easy (1) — manageable, minor friction ────────────────────
        ["MonsterBuffChaosDamage"]          = 1,   // Chaos Damage
        ["MonsterBuffIncreasedDamage"]      = 1,   // Increased Damage
        ["MonsterBuffResistances"]          = 1,   // Monster Resistances
        ["MonsterBuffLife"]                 = 1,   // Increased Life
        ["PlayerDebuffReducedDamage"]       = 1,   // Reduced Damage
        ["MonsterHitsAreCriticalStrikes"]   = 1,   // Deadly Monsters
        ["FlamespitterDaemon1"]             = 1,   // Raging Dead I (more mob spawns; bot likes more targets)
        ["ChaosCloudDaemon1"]               = 1,   // Choking Miasma I (small chaos DoT cloud — bot anchors → some health pressure)

        // ── Medium (3) — noticeable danger ───────────────────────────
        ["FlamespitterDaemon2"]             = 3,   // Raging Dead II
        ["ChaosCloudDaemon2"]               = 3,   // Choking Miasma II
        ["ChaosCloudDaemon3"]               = 3,   // Choking Miasma III — same family, scaled tier
        ["FlamespitterDaemon3"]             = 3,   // Raging Dead III (speculative; danger likely similar)
        ["PlayerDebuffReducedRecovery"]     = 3,   // Reduced Recovery
        ["PlayerDebuffIncreasingVulnerability"] = 5, // Escalating Damage Taken (ramps to 50%)

        // ── Hard (5) — build-dependent ────────────────────────────────
        ["PlayerDebuffNoLeech"]             = 5,   // No Leech
        ["PlayerDebuffNoRegen"]             = 5,   // No Regeneration

        // ── SKIP (999) — never auto-pick ─────────────────────────────
        // Two distinct "Ruin" families exist in PoE Ultimatum:
        //   • RevenantDaemon* — "Stalking Ruin" (an invulnerable shade follows you)
        //   • MonstersApplyRuin* — "Ruin" (monsters apply Ruin stacks on hit)
        // Both auto-fail the encounter at stack cap; bot positions poorly to avoid either,
        // so all tiers of both families are NEVER-TAKE per user's "no Ruin modifier" rule.
        ["RevenantDaemon1"]                 = 999, // Stalking Ruin I
        ["RevenantDaemon2"]                 = 999, // Stalking Ruin II
        ["RevenantDaemon3"]                 = 999, // Stalking Ruin III
        ["RevenantDaemon4"]                 = 999, // Stalking Ruin IV
        ["MonstersApplyRuin1"]              = 999, // Ruin I
        ["MonstersApplyRuin2"]              = 999, // Ruin II — observed in round 2 of Mesa Survive (2026-05-18)
        ["MonstersApplyRuin3"]              = 999, // Ruin III
        ["MonstersApplyRuin4"]              = 999, // Ruin IV
        // Blood Altar — same Ruin stacking via standing in altar zone.
        ["AltarDaemon1"]                    = 999, // Blood Altar I
        ["AltarDaemon2"]                    = 999, // Blood Altar II
        ["AltarDaemon3"]                    = 999, // Blood Altar III

        // Flask-disruption — kills the cast-on-stunned chieftain's sustain loop.
        ["PlayerDebuffLimitedFlasks"]       = 999, // Limited Flasks (= flasks-disabled, user explicit)
        ["PlayerDebuffHinderedMsOnFlaskUse"]= 999, // Hindering Flasks (-50% MS on flask use; build flasks constantly)

        // Charge / buff nullification — strips the build's defensive layers.
        ["PlayerDebuffNullification"]       = 999, // Nullification (removes charges + buffs)
    };

    /// <summary>
    /// Resolve danger for a single modifier id. User overrides win; falls back to baseline
    /// table; final fallback is <see cref="DefaultDanger"/> (= 3, Medium) for unknown mods.
    /// </summary>
    public static int GetDanger(string modId, IReadOnlyDictionary<string, int>? userOverrides = null)
    {
        if (userOverrides is not null && userOverrides.TryGetValue(modId, out var u)) return u;
        if (Defaults.TryGetValue(modId, out var d)) return d;
        return DefaultDanger;
    }

    /// <summary>
    /// Resolve danger with live display text as a second safety channel. A league can add ids
    /// before this catalog is updated; explicit Ruin families and the RF build's worst defensive
    /// breakers must still fail closed. Exact-id overrides remain available for ordinary mods.
    /// </summary>
    public static int GetDanger(
        string modId,
        string displayName,
        string description,
        IReadOnlyDictionary<string, int>? userOverrides = null)
    {
        if (IsRuinFamily(modId, displayName, description)) return BlockedValue;
        if (userOverrides is not null && userOverrides.TryGetValue(modId, out var user)) return user;

        if (ContainsAny(displayName,
                "Restless Ground", "Shattered Shield", "Drought", "Hindering Flasks", "Impurity"))
            return BlockedValue;
        if (ContainsAny(displayName,
                "Overwhelming Monsters", "Siphoned Charges", "Waning Spirit", "Profane Monsters"))
            return 10;
        if (displayName.Contains("Reduced Recovery II", StringComparison.OrdinalIgnoreCase))
            return 10;
        return GetDanger(modId);
    }

    private static bool IsRuinFamily(string id, string name, string description)
        => id.StartsWith("RevenantDaemon", StringComparison.OrdinalIgnoreCase)
        || id.StartsWith("MonstersApplyRuin", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Ruin", StringComparison.OrdinalIgnoreCase)
        || description.Contains("Ruin", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsAny(string value, params string[] needles)
        => needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Greedy mod selection. Given the ordered list of available mod ids for a round,
    /// returns the index of the lowest-danger pickable option, or -1 if every option is
    /// at <see cref="BlockedValue"/>. Ties broken by first-found (no shuffle — deterministic).
    /// </summary>
    public static int PickBestModifier(IReadOnlyList<string> available, IReadOnlyDictionary<string, int>? userOverrides = null)
    {
        var bestIdx = -1;
        var bestDanger = BlockedValue;
        for (var i = 0; i < available.Count; i++)
        {
            var d = GetDanger(available[i], userOverrides);
            if (d >= BlockedValue) continue;
            if (d < bestDanger) { bestDanger = d; bestIdx = i; }
        }
        return bestIdx;
    }

    /// <summary>Parse a string-list of "modId=N" entries into an override dictionary. Skips malformed rows.</summary>
    public static Dictionary<string, int> ParseOverrides(IEnumerable<string> rows)
    {
        var dict = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row)) continue;
            var eq = row.IndexOf('=');
            if (eq <= 0 || eq >= row.Length - 1) continue;
            var key = row[..eq].Trim();
            if (!int.TryParse(row[(eq + 1)..].Trim(), out var v)) continue;
            dict[key] = v;
        }
        return dict;
    }
}
