namespace BubblesBot.Bot.Settings;

/// <summary>
/// User-controllable loot filter knobs. Mounted at <see cref="BotSettings.Loot"/>; the web-UI
/// schema walker recurses in via <see cref="SettingNestedAttribute"/> and renders each property
/// inline. Persisted as a nested JSON object under <c>"loot"</c>.
///
/// <para>The filter pipeline that consumes these lives in <c>Behaviors/Loot/ValueFilter.cs</c>.
/// Category-specific gates enable their min-chaos thresholds. Unique filtering defaults on
/// at 10 chaos to avoid filling inventory with known low-value equipment; genuinely unknown
/// prices still fail open so fresh-league items are not silently destroyed.</para>
/// </summary>
public sealed class LootSettings
{
    [Setting("Loot", "Loot hotkey",
        "Win32 virtual-key code. Hold this key to make the looter click visible items. Default 0xC0 = backtick (`).")]
    [SettingKeycode]
    public int HotkeyVk { get; set; } = 0xC0;

    [Setting("Loot", "Min item value (chaos)",
        "Generic chaos threshold for items without a category-specific rule (rares, magic items, currency that's not on the always-take allowlist). 0 = take everything.")]
    [SettingRange(0, 100, 1)]
    public float MinChaosValue { get; set; } = 0f;

    // ── Quest items ─────────────────────────────────────────────────
    [Setting("Loot: Quest items", "Ignore quest items",
        "Skip items whose path contains '/Quest' (Trial keys, lab keys, league-mechanic quest drops). Usually you don't want to pick these up — they auto-pick on touch in most leagues.")]
    public bool IgnoreQuestItems { get; set; } = true;

    // ── Chest denylist ─────────────────────────────────────────────
    [Setting("Loot: Chests", "Chest-name denylist",
        "Render names that the loot key should skip. Plain 'Chest' (and other generic trash containers like 'Vase', 'Urn', 'Barrel') drops nothing valuable but slows the bot. Special chests (Strongboxes, Heist chests, Blight chests) carry distinct render names — those still open. Case-insensitive exact match against the chest's Render.Name.")]
    [SettingStringList("e.g. Vase")]
    public List<string> ChestRenderNameDenylist { get; set; } = new() { "Chest" };

    // ── Uniques ─────────────────────────────────────────────────────
    [Setting("Loot: Uniques", "Filter uniques by value",
        "When on, uniques are skipped unless their poe.ninja price meets the threshold (or they match the must-loot list). When off, ALL uniques are picked up (legacy behavior).")]
    public bool FilterUniques { get; set; } = true;

    [Setting("Loot: Uniques", "Min unique chaos value",
        "Flat chaos threshold. Uniques priced below this are skipped (unless allowlisted).")]
    [SettingRange(0, 500, 1)]
    public int MinUniqueChaosValue { get; set; } = 10;

    [Setting("Loot: Uniques", "Min chaos per slot",
        "Per-slot floor: chaos value divided by inventory slots (width × height). 0 = disabled. Filters out cheap large-footprint uniques like maps and bows.")]
    [SettingRange(0, 50, 1)]
    public int MinChaosPerSlot { get; set; } = 0;

    [Setting("Loot: Uniques", "Must-loot uniques",
        "Unique names that bypass the value filter. Case-insensitive substring match against the unique name (e.g. 'Headhunter', 'Mageblood').")]
    [SettingStringList("e.g. Headhunter")]
    public List<string> MustLootUniques { get; set; } = new()
    {
        "Voices", "Megalomaniac", "Split Personality",
    };

    [Setting("Loot: Overrides", "Always-loot items",
        "Any item name that bypasses all value filters. Case-insensitive substring match. Use this for valuable bases or league-specific drops, not only uniques.")]
    [SettingStringList("e.g. Sacred Orb")]
    public List<string> AlwaysLootItems { get; set; } = new();

    [Setting("Loot: Overrides", "Price overrides",
        "Manual chaos values in Name=Value form. Overrides poe.ninja for pickup decisions and profit reporting (for example: Voices=50).")]
    [SettingStringList("e.g. Voices=50")]
    public List<string> PriceOverrides { get; set; } = new();

    // ── Cluster jewels ──────────────────────────────────────────────
    [Setting("Loot: Cluster jewels", "Filter cluster jewels by value",
        "When on, cluster jewels are skipped unless their poe.ninja price (looked up by enchant + passive count) meets the threshold.")]
    public bool FilterClusterJewels { get; set; } = true;

    [Setting("Loot: Cluster jewels", "Min cluster jewel chaos value",
        "Chaos threshold for cluster jewels.")]
    [SettingRange(0, 500, 1)]
    public int MinClusterJewelChaosValue { get; set; } = 15;

    // ── Skill gems ──────────────────────────────────────────────────
    [Setting("Loot: Gems", "Filter skill gems by value",
        "When on, gems are skipped unless their poe.ninja price (by level + quality) meets the threshold. The 20%-quality bypass below can override this.")]
    public bool FilterSkillGems { get; set; } = true;

    [Setting("Loot: Gems", "Min gem chaos value",
        "Chaos threshold for skill gems.")]
    [SettingRange(0, 500, 1)]
    public int MinGemChaosValue { get; set; } = 15;

    [Setting("Loot: Gems", "Always loot 20% quality gems",
        "Any gem at 20%+ quality is picked up regardless of value (vendor-recipe / GCP value).")]
    public bool AlwaysLoot20QualityGems { get; set; } = true;

    // ── Synthesised items ───────────────────────────────────────────
    [Setting("Loot: Synthesised", "Filter synthesised items",
        "When on, synthesised items are skipped unless their implicit-mod text contains one of the whitelist substrings.")]
    public bool FilterSynthesisedItems { get; set; } = false;

    [Setting("Loot: Synthesised", "Implicit whitelist",
        "Case-insensitive substrings checked against the synthesised implicit mod. Any match = pick up.")]
    [SettingStringList("e.g. Damage")]
    public List<string> SynthesisedWhitelist { get; set; } = new() { "Damage", "Life", "Resistance" };
}
