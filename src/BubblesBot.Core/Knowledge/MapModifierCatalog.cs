using System.Text.RegularExpressions;

namespace BubblesBot.Core.Knowledge;

public enum MapModifierDisposition
{
    Allow = 0,
    Avoid = 1,
    Never = 2,
}

public sealed record MapModifierDefinition(
    string Key,
    string Category,
    string DisplayName,
    MapModifierDisposition DefaultDisposition,
    IReadOnlyList<string> TooltipTemplates)
{
    internal IReadOnlyList<Regex> TooltipPatterns { get; init; } =
        TooltipTemplates.Select(MapModifierCatalog.CompileTemplate).ToArray();
}

public readonly record struct MapModifierMatch(
    MapModifierDefinition Definition,
    MapModifierDisposition Disposition,
    IReadOnlyList<string> MatchedLines);

public readonly record struct MapModifierVerdict(IReadOnlyList<MapModifierMatch> Matches)
{
    public IReadOnlyList<MapModifierMatch> Avoided
        => Matches.Where(x => x.Disposition == MapModifierDisposition.Avoid).ToArray();
    public IReadOnlyList<MapModifierMatch> Never
        => Matches.Where(x => x.Disposition == MapModifierDisposition.Never).ToArray();
    public bool Runnable => Never.Count == 0;
}

/// <summary>
/// Patch-aware semantic vocabulary for map modifiers. Display templates are the seed/fallback
/// identity; live raw mod names and stable stat keys can be attached to the semantic keys later
/// without changing build profiles. Numeric ranges in templates match any live integer roll.
/// </summary>
public static class MapModifierCatalog
{
    public static readonly IReadOnlyList<MapModifierDefinition> Known =
    [
        D("reflect.physical", "Reflect", Never, "Monsters reflect (13-18)% of Physical Damage"),
        D("reflect.elemental", "Reflect", Never, "Monsters reflect (13-18)% of Elemental Damage"),
        D("defence.reduced-aura-effect", "Defences and resistances", Allow, "Players have (25-60)% reduced effect of Non-Curse Auras from Skills"),
        D("defence.maximum-resistance-penalty", "Defences and resistances", Allow, "Players have -(5-12)% to all maximum Resistances"),
        D("recovery.no-regeneration", "Recovery and leech", Never, "Players cannot Regenerate Life, Mana or Energy Shield"),
        D("recovery.less-life-energy-shield", "Recovery and leech", Allow, "Players have (20-60)% less Recovery Rate of Life and Energy Shield"),
        D("recovery.no-leech", "Recovery and leech", Allow, "Monsters cannot be Leeched from"),
        D("monster.critical-strikes", "Monster damage", Allow,
            "Monsters have (160-400)% increased Critical Strike Chance",
            "+(30-45)% to Monster Critical Strike Multiplier"),
        D("monster.extra-chaos-wither", "Monster damage", Allow,
            "Monsters gain (21-35)% of their Physical Damage as Extra Chaos Damage",
            "Monsters Inflict Withered for 2 seconds on Hit"),
        D("monster.extra-fire", "Monster damage", Allow, "Monsters deal (50-110)% extra Physical Damage as Fire"),
        D("monster.extra-cold", "Monster damage", Allow, "Monsters deal (50-110)% extra Physical Damage as Cold"),
        D("monster.extra-lightning", "Monster damage", Allow, "Monsters deal (50-110)% extra Physical Damage as Lightning"),
        D("monster.additional-projectiles", "Projectiles", Allow, "Monsters fire 2 additional Projectiles"),
        D("boss.damage-speed", "Boss modifiers", Allow,
            "Unique Boss deals (15-25)% increased Damage",
            "Unique Boss has (20-30)% increased Attack and Cast Speed"),
        D("monster.speed", "Monster speed", Allow,
            "(15-30)% increased Monster Movement Speed",
            "(20-45)% increased Monster Attack Speed",
            "(20-45)% increased Monster Cast Speed"),
        D("boss.life-area", "Boss modifiers", Allow,
            "Unique Boss has (25-35)% increased Life",
            "Unique Boss has (45-70)% increased Area of Effect"),
        D("monster.area-of-effect", "Monster damage", Allow, "Monsters have (45-100)% increased Area of Effect"),
        D("monster.avoid-poison-impale-bleeding", "Ailments", Allow, "Monsters have a (20-50)% chance to avoid Poison, Impale, and Bleeding"),
        D("monster.poison-on-hit", "Ailments", Allow, "Monsters Poison on Hit"),
        D("monster.chain", "Projectiles", Allow, "Monsters' skills Chain 2 additional times"),
        D("monster.increased-damage", "Monster damage", Allow, "(14-40)% increased Monster Damage"),
        D("defence.suppression-accuracy", "Defences and resistances", Allow,
            "Players have -(10-20)% to amount of Suppressed Spell Damage Prevented",
            "Monsters have (30-50)% increased Accuracy Rating"),
        D("curse.reduced-effect-on-monsters", "Curses", Allow, "(25-60)% less effect of Curses on Monsters"),
        D("curse.enfeeble", "Curses", Allow, "Players are Cursed with Enfeeble"),
        D("curse.vulnerability", "Curses", Allow, "Players are Cursed with Vulnerability"),
        D("curse.temporal-chains", "Curses", Allow, "Players are Cursed with Temporal Chains"),
        D("curse.elemental-weakness", "Curses", Allow, "Players are Cursed with Elemental Weakness"),
        D("ground.consecrated", "Ground effects", Allow, "Area has patches of Consecrated Ground"),
        D("ground.desecrated", "Ground effects", Allow, "Area has patches of desecrated ground"),
        D("ground.shocked", "Ground effects", Allow, "Area has patches of Shocked Ground which increase Damage taken by (20-50)%"),
        D("ground.chilled", "Ground effects", Allow, "Area has patches of Chilled Ground"),
        D("ground.burning", "Ground effects", Allow, "Area has patches of Burning Ground"),
        D("defence.less-block-armour", "Defences and resistances", Allow,
            "Players have (20-40)% reduced Chance to Block",
            "Players have (20-30)% less Armour"),
        D("monster.spell-suppression", "Defences and resistances", Allow, "Monsters have +(30-100)% chance to Suppress Spell Damage"),
        D("monster.reduced-extra-critical-damage", "Defences and resistances", Allow, "Monsters take (25-45)% reduced Extra Damage from Critical Strikes"),
        D("monster.extra-energy-shield", "Defences and resistances", Allow, "Monsters gain (20-80)% of Maximum Life as Extra Maximum Energy Shield"),
        D("flask.reduced-charges", "Recovery and leech", Allow, "Players gain (30-50)% reduced Flask Charges"),
        D("monster.avoid-elemental-ailments", "Ailments", Allow, "Monsters have (30-70)% chance to Avoid Elemental Ailments"),
        D("monster.physical-damage-reduction", "Defences and resistances", Allow, "+(20-40)% Monster Physical Damage Reduction"),
        D("exposure.cannot-inflict", "Defences and resistances", Allow, "Players cannot inflict Exposure"),
        D("monster.hexproof", "Curses", Allow, "Monsters are Hexproof"),
        D("monster.resistances", "Defences and resistances", Allow,
            "+(15-25)% Monster Chaos Resistance",
            "+(20-40)% Monster Elemental Resistances"),
        D("monster.more-life", "Monster life", Allow, "(20-100)% more Monster Life"),
        D("monster.unwavering-life", "Monster life", Allow,
            "Monsters cannot be Stunned",
            "(15-30)% more Monster Life"),
        D("monster.always-ignite", "Ailments", Allow, "All Monster Damage from Hits always Ignites"),
        D("monster.impale-on-hit", "Ailments", Allow, "Monsters' Attacks have (25-60)% chance to Impale on Hit"),
        D("monster.elemental-ailments-on-hit", "Ailments", Allow, "Monsters have a (15-20)% chance to Ignite, Freeze and Shock on Hit"),
        D("player.buffs-expire-faster", "Player penalties", Allow, "Buffs on Players expire (30-100)% faster"),
        D("player.less-cooldown-recovery", "Player penalties", Allow, "Players have (20-40)% less Cooldown Recovery Rate"),
        D("boss.possessed", "Boss modifiers", Allow, "Unique Bosses are Possessed"),
        D("boss.twinned", "Boss modifiers", Allow, "Area contains two Unique Bosses"),
        D("monster.unstoppable", "Monster speed", Allow,
            "Monsters' Action Speed cannot be modified to below Base Value",
            "Monsters' Movement Speed cannot be modified to below Base Value",
            "Monsters cannot be Taunted"),
        D("player.less-accuracy", "Player penalties", Allow, "Players have (15-25)% less Accuracy Rating"),
        D("monster.steal-charges", "Charges", Allow, "Monsters steal Power, Frenzy and Endurance charges on Hit"),
        D("monster.frenzy-charge-on-hit", "Charges", Allow, "Monsters gain a Frenzy Charge on Hit"),
        D("monster.endurance-charge-on-hit", "Charges", Allow, "Monsters gain an Endurance Charge on Hit"),
        D("monster.power-charge-on-hit", "Charges", Allow, "Monsters gain a Power Charge on Hit"),
        D("player.less-area-of-effect", "Player penalties", Allow, "Players have (15-25)% less Area of Effect"),
        D("monster.maim-on-hit", "Ailments", Allow, "Monsters Maim on Hit with Attacks"),
        D("monster.hinder-on-hit", "Ailments", Allow, "Monsters Hinder on Hit with Spells"),
        D("monster.blind-on-hit", "Ailments", Allow, "Monsters Blind on Hit"),
        D("area.many-totems", "Area population", Allow, "Area contains many Totems"),
        D("area.increased-variety", "Area population", Allow, "Area has increased monster variety"),
        D("area.cultists-of-kitava", "Area population", Allow, "Area is inhabited by Cultists of Kitava"),
        D("area.ranged-monsters", "Area population", Allow, "Area is inhabited by ranged monsters"),
        D("area.lunaris-fanatics", "Area population", Allow, "Area is inhabited by Lunaris fanatics"),
        D("area.undead", "Area population", Allow, "Area is inhabited by Undead"),
        D("area.humanoids", "Area population", Allow, "Area is inhabited by Humanoids"),
        D("area.goatmen", "Area population", Allow, "Area is inhabited by Goatmen"),
        D("area.skeletons", "Area population", Allow, "Area is inhabited by Skeletons"),
        D("area.solaris-fanatics", "Area population", Allow, "Area is inhabited by Solaris fanatics"),
        D("area.sea-witches", "Area population", Allow, "Area is inhabited by Sea Witches and their Spawn"),
        D("area.demons", "Area population", Allow, "Area is inhabited by Demons"),
        D("area.abominations", "Area population", Allow, "Area is inhabited by Abominations"),
        D("area.animals", "Area population", Allow, "Area is inhabited by Animals"),
        D("area.ghosts", "Area population", Allow, "Area is inhabited by Ghosts"),
        D("area.rare-monsters", "Area population", Allow, "(20-30)% increased number of Rare Monsters"),
        D("area.magic-monsters", "Area population", Allow, "(20-30)% increased Magic Monsters"),
    ];

    private static readonly IReadOnlyDictionary<string, MapModifierDefinition> ByKey =
        Known.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);

    public static readonly IReadOnlyList<(string Label, int Value)> Policies =
    [
        ("Allow", (int)MapModifierDisposition.Allow),
        ("Avoid", (int)MapModifierDisposition.Avoid),
        ("Never run", (int)MapModifierDisposition.Never),
    ];

    public static MapModifierVerdict EvaluateTooltip(
        IEnumerable<string> tooltipLines,
        IEnumerable<string>? policyOverrides = null)
    {
        var lines = tooltipLines
            .SelectMany(x => x.Split(['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var overrides = ParseOverrides(policyOverrides);
        var matches = new List<MapModifierMatch>();
        foreach (var definition in Known)
        {
            var matched = new List<string>();
            foreach (var pattern in definition.TooltipPatterns)
            {
                var line = lines.FirstOrDefault(pattern.IsMatch);
                if (line is null)
                {
                    matched.Clear();
                    break;
                }
                matched.Add(line);
            }
            if (matched.Count == 0) continue;
            var disposition = overrides.TryGetValue(definition.Key, out var value)
                ? value
                : definition.DefaultDisposition;
            matches.Add(new MapModifierMatch(definition, disposition, matched));
        }
        return new MapModifierVerdict(matches);
    }

    public static IReadOnlyDictionary<string, MapModifierDisposition> ParseOverrides(
        IEnumerable<string>? entries)
    {
        var parsed = new Dictionary<string, MapModifierDisposition>(StringComparer.OrdinalIgnoreCase);
        if (entries is null) return parsed;
        foreach (var row in entries)
        {
            var equals = row.IndexOf('=');
            if (equals <= 0) continue;
            var key = row[..equals].Trim();
            if (!ByKey.ContainsKey(key)
                || !int.TryParse(row[(equals + 1)..], out var raw)
                || !Enum.IsDefined(typeof(MapModifierDisposition), raw))
                continue;
            parsed[key] = (MapModifierDisposition)raw;
        }
        return parsed;
    }

    public static string? ValidateOverrides(IEnumerable<string>? entries)
    {
        if (entries is null) return "expected a modifier-policy list";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in entries)
        {
            var equals = row.IndexOf('=');
            var key = equals > 0 ? row[..equals].Trim() : string.Empty;
            if (equals <= 0 || !ByKey.ContainsKey(key))
                return $"unknown or malformed modifier policy '{row}'";
            if (!int.TryParse(row[(equals + 1)..], out var raw)
                || !Enum.IsDefined(typeof(MapModifierDisposition), raw))
                return $"modifier policy for '{key}' must be 0 (Allow), 1 (Avoid), or 2 (Never)";
            if (!seen.Add(key)) return $"duplicate modifier policy for '{key}'";
        }
        return null;
    }

    internal static Regex CompileTemplate(string template)
    {
        if (template.Equals("Players cannot Regenerate Life, Mana or Energy Shield",
                StringComparison.OrdinalIgnoreCase))
            return new Regex(
                @"^Players cannot Regenerate (?:Life, Mana or Energy Shield|Life or Mana|Mana)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        var pattern = new System.Text.StringBuilder("^");
        var cursor = 0;
        foreach (Match range in Regex.Matches(template, @"\(\d+-\d+\)"))
        {
            pattern.Append(Regex.Escape(template[cursor..range.Index]));
            // Advanced tooltips may append the selected tier's range after the rolled value,
            // e.g. "365(360-400)%". Header lines normally show only "365%".
            pattern.Append(@"\d+(?:\([-0-9]+\))?");
            cursor = range.Index + range.Length;
        }
        pattern.Append(Regex.Escape(template[cursor..]));
        pattern.Append('$');
        return new Regex(pattern.ToString(),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    private static MapModifierDefinition D(
        string key,
        string category,
        MapModifierDisposition disposition,
        params string[] tooltipTemplates)
        => new(key, category, string.Join(" · ", tooltipTemplates), disposition, tooltipTemplates);

    private const MapModifierDisposition Allow = MapModifierDisposition.Allow;
    private const MapModifierDisposition Never = MapModifierDisposition.Never;
}
