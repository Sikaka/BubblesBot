namespace BubblesBot.Bot.Settings;

/// <summary>
/// Ultimatum-mode knobs. Mounted at <see cref="BotSettings.Ultimatum"/>; the web-UI walker
/// recurses in via <see cref="SettingNestedAttribute"/>. Persisted under <c>"ultimatum"</c>.
///
/// <para>The mode picks the lowest-danger modifier each round (greedy), accumulates danger
/// across rounds, and bails out when <see cref="DangerThreshold"/> is exceeded or
/// <see cref="MaxWaves"/> is reached. Default values mirror AutoExile's softcore-mapping
/// baseline; per-mod weights are tunable via <see cref="ModDangerOverrides"/>.</para>
/// </summary>
public sealed class UltimatumSettings
{
    [Setting("Ultimatum", "Max waves",
        "Take the reward and exit after this many rounds. AutoExile defaults to 10 (the full Trial of Ascendancy length); reduce for safer / faster cycling.")]
    [SettingRange(1, 10, 1)]
    public int MaxWaves { get; set; } = 10;

    [Setting("Ultimatum", "Danger threshold",
        "Cumulative danger budget across rounds. The next round's chosen modifier danger is added to the running total — once total + next exceeds this number, the bot takes the reward and exits. Lower = bot bails earlier.")]
    [SettingRange(5, 100, 5)]
    public int DangerThreshold { get; set; } = 30;

    [Setting("Ultimatum", "Orbit radius (grid)",
        "How close the bot stays to the altar during combat. Smaller = anchors tightly (better for ranged builds and Limited-Arena rounds); larger = roams more (melee). Mirrors AutoExile's OrbitRadius.")]
    [SettingRange(8, 60, 2)]
    public int OrbitRadius { get; set; } = 25;

    [Setting("Ultimatum", "Abandon on 'stand in circle'",
        "Trial of Glory variant requires standing inside a small shrinking circle while monsters attack. Bots position poorly for it — abandon the encounter when this variant is detected, leaving the map without rewards.")]
    public bool AbandonOnStandInCircle { get; set; } = true;

    [Setting("Ultimatum", "Modifier danger table",
        "Per-modifier difficulty. Defaults are baked in for a cast-on-stunned chieftain build; change a tier to override. The bot picks the lowest-danger mod each round; any mod set to NEVER causes the run to be abandoned if all three offered mods are NEVER. Storage format is 'ModId=N' under the hood — but you don't need to know the IDs.")]
    [SettingModTable]
    public List<string> ModDangerOverrides { get; set; } = new();
}
