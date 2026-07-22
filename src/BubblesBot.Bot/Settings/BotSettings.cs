namespace BubblesBot.Bot.Settings;

/// <summary>
/// User-controllable bot configuration. Persisted to <c>%APPDATA%/BubblesBot/config.json</c>
/// and synced live via the web UI. Every annotated property surfaces in the schema endpoint.
///
/// <para>Convention: defaults here are the values a fresh user gets. Don't store sensitive
/// or runtime-only state on this object — it's serialized verbatim to disk.</para>
/// </summary>
public sealed class BotSettings
{
    [Setting("General", "Bot active",
        "Master enable/disable for all bot actions. Persists across restarts. The Insert hotkey toggles this in-game.")]
    public bool BotActive { get; set; } = false;

    [Setting("General", "Maximum run time (minutes)",
        "Bot-wide armed-session limit across hideout, loading screens, and all zones. At the limit automation disarms safely. 0 disables the limit.")]
    [SettingRange(0, 1440, 15)]
    public int MaxRunMinutes { get; set; } = 0;

    /// <summary>
    /// Which production mode runs while armed. Overlay/manual keeps autonomous movement and
    /// combat off while retaining overlay, flask, and hold-to-loot assistance.
    /// </summary>
    [Setting("General", "Mode",
        "Which behavior the bot runs while armed.")]
    [SettingOptions(
        "Overlay / manual", "0",
        "Map farming",     "4",
        "Blight",          "5",
        "Simulacrum",      "6",
        "Guardian Rota",   "7")]
    public int ActiveMode { get; set; } = 0;

    /// <summary>
    /// Id of the active farming strategy (per-character; strategies themselves are global).
    /// Deliberately NOT <c>[Setting]</c>-annotated — it never appears in the generated settings
    /// UI and is set only through the strategy activation endpoint, but it IS persisted so the
    /// active strategy survives restarts. Empty means no strategy is selected.
    /// </summary>
    public string ActiveStrategyId { get; set; } = "";

    [Setting("Guardian Rota", "Target rotations",
        "Complete this many full four-Guardian plus The Formed rotations before disarming.")]
    [SettingRange(1, 100, 1)]
    public int GuardianTargetRotations { get; set; } = 3;

    [Setting("Guardian Rota", "Minimum invitation quantity",
        "The Formed is never activated below this item-quantity value.")]
    [SettingRange(0, 150, 1)]
    public int GuardianInvitationMinQuantity { get; set; } = 65;

    [Setting("Guardian Rota", "Preferred invitation quantity",
        "Reported as the preferred target; the minimum remains the hard activation gate.")]
    [SettingRange(0, 150, 1)]
    public int GuardianInvitationPreferredQuantity { get; set; } = 75;

    [Setting("Guardian Rota", "Maximum Chaos rerolls",
        "Maximum Chaos Orbs spent on one map or invitation before the route stops fail-closed.")]
    [SettingRange(1, 50, 1)]
    public int GuardianMaxChaosRerolls { get; set; } = 10;

    [Setting("Guardian Rota", "Dump tab name",
        "Loot is deposited here after each completed Formed invitation; Guardian maps and rolling currency remain carried.")]
    public string GuardianDumpTabName { get; set; } = "Dump";

    [Setting("Simulacrum", "Minimum delay between waves (s)",
        "Wait at least this long after a wave ends before clicking the monolith again. Loot settling can extend the delay.")]
    [SettingRange(2, 20, 1)]
    public float SimulacrumMinWaveDelaySeconds { get; set; } = 5f;

    [Setting("Simulacrum", "Wave start timeout (s)",
        "Maximum navigation/click/confirmation time after rewards before a wave start fails closed.")]
    [SettingRange(10, 60, 5)]
    public float SimulacrumStartTimeoutSeconds { get; set; } = 30f;

    [Setting("Simulacrum", "Wave timeout (s)",
        "Hard active-wave limit. Pause/loading time and reward looting do not count. At the limit the bot abandons this instance and starts a fresh supplied Simulacrum.")]
    [SettingRange(60, 600, 15)]
    public float SimulacrumWaveTimeoutSeconds { get; set; } = 150f;

    [Setting("Simulacrum", "Maximum deaths per wave",
        "Deaths allowed in one wave. Earlier deaths re-enter through an existing portal; reaching this count abandons the instance and starts a fresh supplied Simulacrum. 0 abandons on the first death.")]
    [SettingRange(0, 6, 1)]
    public int SimulacrumMaxDeaths { get; set; } = 2;

    [Setting("Simulacrum", "Stash at occupied cells",
        "Between waves, deposit when this many of the 60 inventory cells are occupied. 0 disables between-wave stashing.")]
    [SettingRange(0, 60, 1)]
    public int SimulacrumStashOccupiedCells { get; set; } = 15;

    [Setting("Simulacrum", "Inventory key",
        "Key used between waves to inspect occupied inventory cells (PoE default: I).")]
    [SettingKeycode]
    public int SimulacrumInventoryKeyVk { get; set; } = 0x49;

    [Setting("Simulacrum", "Loot quiet window (s)",
        "After a physical arena sweep finds no actionable item, wait this long near the monolith before starting the next wave or exiting.")]
    [SettingRange(2, 15, 1)]
    public float SimulacrumLootQuietSeconds { get; set; } = 5f;

    [Setting("Simulacrum", "Target runs",
        "Complete this many full Simulacrums before disarming. 0 continues until the bot-wide run-time limit or supplies are exhausted.")]
    [SettingRange(0, 100, 1)]
    public int SimulacrumTargetRuns { get; set; } = 0;

    [Setting("Simulacrum", "Discard existing run once",
        "One-shot recovery control. If armed inside an unwanted Simulacrum, exit its fresh portal, confirm hideout, ignore that portal set, and start a new supplied run. Clears automatically in hideout.")]
    public bool SimulacrumDiscardExistingRun { get; set; } = false;

    [Setting("Simulacrum", "Supply tab name",
        "Named stash tab containing Simulacrums. The loop switches here before each hideout withdrawal.")]
    public string SimulacrumSupplyTabName { get; set; } = "Deli";

    [Setting("Simulacrum", "Dump tab name",
        "Regular, Premium, or Quad stash tab used for between-wave loot. Specialized affinity tabs are rejected.")]
    public string SimulacrumDumpTabName { get; set; } = "Dump";

    // Map-farming configuration moved to farming-strategy documents (Strategies tab). The old
    // MapFarm*/StackedDeck*/Take*/Ritual*/AltarPolicy/ExplorationDonePercent properties were
    // removed here; a one-time seed migration (LegacyFarmSettings + LegacySettingsMigration)
    // carries a user's prior values into the two built-in strategies on first run.

    [Setting("Blight", "Resume existing portal once",
        "One-shot recovery: while in hideout, enter an already-open map portal instead of consuming another carried map. Clears automatically after entering. Enable only when resuming a known Blight instance after a disconnect/death.")]
    public bool BlightResumeExistingPortalOnce { get; set; } = false;

    [Setting("Blight", "Supply tab name",
        "Named stash tab containing pre-rolled Blight-ravaged maps. Exactly one positively identified map is withdrawn before each run.")]
    public string BlightSupplyTabName { get; set; } = "Supplies";

    [Setting("Blight", "Dump tab name",
        "Regular, Premium, or Quad stash tab used to store loot between runs. Specialized affinity tabs are rejected.")]
    public string BlightDumpTabName { get; set; } = "Dump";

    [Setting("Blight", "Defend radius (grid)",
        "How close the bot stays to the pump while defending. Smaller = tighter anchoring (better for ranged builds); larger = roams more (melee).")]
    [SettingRange(10, 80, 5)]
    public float BlightDefendRadius { get; set; } = 30f;

    [Setting("Blight", "Defend timeout (s)",
        "After the pump activates, defend for at most this long before giving up and moving on to post-encounter sweep. Most blights end in 90-180 s; 240 s tolerates slow ones without aborting too early.")]
    [SettingRange(30, 360, 10)]
    public float BlightDefendTimeoutSeconds { get; set; } = 240f;

    [Setting("Blight", "Post-sweep duration (s)",
        "Compatibility control for post-timer cleanup. Zero disables cleanup; any positive value enables progress-based reachable-map exploration until no monsters remain. Cleanup no longer returns to the pump merely because this many seconds elapsed.")]
    [SettingRange(0, 60, 5)]
    public float BlightPostSweepSeconds { get; set; } = 25f;

    [Setting("Blight", "Post-timer delay (s)",
        "After the encounter timer hits 0:00, hold position for this long before evaluating the quiet-window. Gives the pump's state machine + late-arrival mobs time to resolve before we start watching for movement to stop.")]
    [SettingRange(0, 15, 1)]
    public float BlightPostTimerDelaySeconds { get; set; } = 3f;

    [Setting("Blight", "Quiet-window before sweep (s)",
        "After the post-timer delay elapses, the bot only transitions to sweep once NO hostile mob in the network bubble has moved for this many consecutive seconds. Catches stuck mobs without leaving the pump prematurely.")]
    [SettingRange(3, 60, 1)]
    public float BlightStuckQuietSeconds { get; set; } = 7f;

    [Setting("Blight", "Cleanup standoff (grid)",
        "During the final monster sweep, walk near a hostile but stop this far away instead of pathing onto its cell. Safer for minion builds that only need to bring allies into engagement range.")]
    [SettingRange(10, 60, 5)]
    public float BlightCleanupStandoffRadius { get; set; } = 30f;

    [Setting("Blight", "Click skip button",
        "While defending, click the encounter's fast-forward (skip) button when it appears. Skips the pre-wave wait period; mobs start arriving immediately.")]
    public bool BlightClickSkipButton { get; set; } = true;

    [Setting("Blight", "Loot chests",
        "When on, the bot walks to each unopened blight chest after the encounter and clicks it.")]
    public bool BlightLootChests { get; set; } = true;

    [Setting("Blight chests", "Currency", "Open currency reward chests.")]
    public bool BlightChestCurrency { get; set; } = true;

    [Setting("Blight chests", "Oils", "Open oil/mushrune reward chests.")]
    public bool BlightChestOils { get; set; } = true;

    [Setting("Blight chests", "Divination cards", "Open divination-card and stacked-deck reward chests.")]
    public bool BlightChestDivinationCards { get; set; } = true;

    [Setting("Blight chests", "Fragments", "Open fragment reward chests.")]
    public bool BlightChestFragments { get; set; } = true;

    [Setting("Blight chests", "Essences", "Open essence reward chests.")]
    public bool BlightChestEssences { get; set; } = true;

    [Setting("Blight chests", "Jewels/trinkets", "Open jewel and trinket reward chests.")]
    public bool BlightChestJewels { get; set; } = true;

    [Setting("Blight chests", "Armour/weapons", "Open generic and special equipment reward chests.")]
    public bool BlightChestEquipment { get; set; } = true;

    [Setting("Blight chests", "Talismans", "Open talisman reward chests.")]
    public bool BlightChestTalismans { get; set; } = true;

    [Setting("Blight chests", "Other", "Open any unclassified Blight reward chest.")]
    public bool BlightChestOther { get; set; } = true;

    [Setting("Blight", "Build defensive towers",
        "Builds pump-local Freeze, Seismic, Meteor, and supporting Empower towers. Freeze, Seismic, and Empower stop at tier 3; Meteor uses the verified tier-4 branch.")]
    public bool BlightBuildTowers { get; set; } = false;

    [Setting("Blight", "Maximum towers",
        "Optional emergency/debug cap for pump-local towers. 0 means unlimited; radius, lane coverage, safety, available foundations, currency, and the Meteor reserve remain the normal limits.")]
    [SettingRange(0, 40, 1)]
    public int BlightMaxTowers { get; set; } = 0;

    [Setting("Blight", "Meteor control-currency reserve",
        "Meteor build/upgrade steps are allowed only when this much tower currency remains afterward. A tier-3 control tower costs 900 total.")]
    [SettingRange(0, 3600, 150)]
    public int BlightMeteorCurrencyReserve { get; set; } = 900;

    [Setting("Blight", "Meteor backfill per control pair",
        "Maximum Meteor towers per completed Freeze+Seismic coverage pair. Meteors may overlap; control and Empower effects do not stack.")]
    [SettingRange(0, 3, 1)]
    public int BlightMeteorBackfillPerControlPair { get; set; } = 2;

    [Setting("Blight", "Tower build radius (grid)",
        "Only use foundations within this grid distance of the cached pump. Keeps construction focused on nearby lane coverage.")]
    [SettingRange(40, 200, 5)]
    public float BlightTowerBuildRadius { get; set; } = 100f;

    [Setting("Blight", "Tower approach distance (grid)",
        "Stop this far from an off-screen foundation. The bot walks only until its label is safely clickable and never requires standing on the foundation.")]
    [SettingRange(10, 60, 5)]
    public float BlightTowerApproachDistance { get; set; } = 25f;

    [Setting("Blight", "Storage slot index",
        "Which atlas-storage slot the bot right-clicks to stage a map. Slot 1 = first stored map (slot 0 is the panel background and is skipped). Falls back to the first available slot if this index isn't populated.")]
    [SettingRange(1, 12, 1)]
    public int BlightStorageSlotIndex { get; set; } = 1;

    [Setting("Map farming", "Portal-scroll key",
        "The keybind that uses a Portal Scroll from inventory (PoE default: F). The loop taps this to open a Town Portal when leaving a map. This is a keybind, so it stays with your build profile — map targeting, scarabs, and mechanics live in the active strategy.")]
    [SettingKeycode]
    public int StackedDeckPortalKeyVk { get; set; } = 0x46;   // VK 'F'

    /// <summary>
    /// All configured skill bindings. Variable-length so users with mouse-button binds, extra
    /// utility skills, or alternate layouts aren't constrained by a fixed slot grid. The Walk
    /// slot is implicit — whichever entry has <c>Role = Walk</c>. Gap-crossing dashes are
    /// every entry with <c>Role = Dash &amp; CanCrossGaps</c>. The web UI renders this as an
    /// add/remove list with per-row controls; serialization is straight JSON.
    /// </summary>
    [Setting("Skills", "Bindings", "Skill bar bindings. Add as many as needed. Walk is whichever slot is tagged Walk; gap-crossers are Dash slots with the cross-gaps toggle on.")]
    [SettingSkills]
    public SkillProfile Skills { get; set; } = new();

    /// <summary>
    /// Flask configuration — variable-length slots with per-slot trigger + threshold.
    /// Wired into the FlaskSystem; auto-fires while combat/movement runs.
    /// </summary>
    [Setting("Flasks", "Bindings", "Flask slot bindings. Each slot has a hotkey, trigger (HP/Mana/Time/BuffMissing), threshold, and per-slot cooldown. Used when auto-detect is OFF.")]
    [SettingFlasks]
    public FlaskProfile Flasks { get; set; } = new();

    [Setting("Flasks", "Auto-detect flasks",
        "Read the belt and drive triggers by the ACTUAL flask in each slot (life→HP, mana/hybrid→mana), pressing that slot's key. Overrides the manual per-slot triggers above. On by default.")]
    public bool AutoDetectFlasks { get; set; } = true;

    [Setting("Flasks", "Auto life threshold (%)", "Fire detected life/hybrid flasks below this HP percent.")]
    [SettingRange(0, 100, 5)]
    public int AutoLifeThresholdPct { get; set; } = 60;

    [Setting("Flasks", "Auto mana threshold (%)", "Fire detected mana/hybrid flasks below this mana percent.")]
    [SettingRange(0, 100, 5)]
    public int AutoManaThresholdPct { get; set; } = 40;

    [Setting("Flasks", "Auto flask cooldown (ms)", "Minimum time between auto life/mana presses per slot.")]
    [SettingRange(500, 10000, 100)]
    public int AutoFlaskCooldownMs { get; set; } = 2000;

    [Setting("Flasks", "Auto-use utility flasks", "Also press detected utility flasks on a timer (off by default — enable per your build).")]
    public bool AutoUseUtilityFlasks { get; set; } = false;

    [Setting("Flasks", "Utility flasks only in combat",
        "When auto-use is enabled, save utility charges until a hostile is inside the combat engage range.")]
    public bool AutoUtilityOnlyInCombat { get; set; } = true;

    [Setting("Flasks", "Auto utility interval (ms)", "Interval between utility flask presses when auto-use is on.")]
    [SettingRange(1000, 15000, 250)]
    public int AutoUtilityIntervalMs { get; set; } = 5000;

    [Setting("Combat", "Engage range (grid)",
        "Distance at which combat mode considers an enemy in range to attack.")]
    [SettingRange(10, 100, 5)]
    public float CombatEngageRange { get; set; } = 50f;

    [Setting("Combat", "Max minutes per zone",
        "Failsafe: if the bot spends longer than this actively running in a single zone, it disarms — bail-early insurance for being totally stuck. 0 disables.")]
    [SettingRange(0, 30, 1)]
    public int MaxZoneMinutes { get; set; } = 8;

    [Setting("Loot", "League name (fallback)",
        "poe.ninja league for item pricing. Normally auto-read from game memory at startup; this value is only used when that read fails. Requires bot restart to take effect.")]
    public string LeagueName { get; set; } = "Standard";

    [Setting("Loot", "Local off-screen loot min chaos",
        "During a bounded mechanic cleanup (currently Ritual), remember accepted drops that scroll just outside label range and finish that local circle before continuing. This never causes an end-of-map backtrack. 0 remembers every accepted local drop.")]
    [SettingRange(0, 200, 1)]
    public float LootBacktrackMinChaos { get; set; } = 5f;

    [Setting("Loot", "In-path loot search radius (grid)",
        "How far mapping may make a short walking detour for an accepted visible drop. Clicking still requires the validated 25-grid interaction envelope. Delirium rejects drops materially behind current fog progress.")]
    [SettingRange(25, 120, 5)]
    public float LootSearchRangeGrid { get; set; } = 70f;

    [Setting("Combat", "Min pack size to detour",
        "After the zone is revealed, exploration only detours/backtracks for clusters of at least this many monsters. Smaller groups and stragglers are ignored — we don't need to kill every mob on the map. 1 = detour for any single mob.")]
    [SettingRange(1, 15, 1)]
    public int MinPackDetourSize { get; set; } = 4;

    [Setting("Combat", "Clear stance",
        "How the map-clear loop deals with monsters. Drive-by = walk past and tap an Attack-role skill (needs one bound). Proximity (aura/RF) = path into each pack and STAND among them until they die from your aura / damaging effects (Righteous Fire, heralds, Cast-on-Stun) — no attack skill required.")]
    [SettingOptions("Drive-by (attack skill)", "0", "Proximity (aura/RF)", "1", "Ranged standoff", "2")]
    public int MapClearStance { get; set; } = 0;

    [Setting("Combat", "Ranged standoff (grid)",
        "Ranged stance only: close until the target is inside this distance with clear terrain line-of-fire, then stop and hold the primary attack.")]
    [SettingRange(10, 90, 5)]
    public float RangedStandoffGrid { get; set; } = 45f;

    [Setting("Combat", "Use dash while closing",
        "Ranged stance only: use a ready gap-crossing Dash for long safe approach steps, not only when terrain forces a gap crossing.")]
    public bool RangedUseDashToClose { get; set; } = true;

    [Setting("Combat", "Ranged kite life (%)",
        "Ranged stance: immediately reposition when life falls below this percentage. Uses a ready Dash first, otherwise walks away. 0 disables health-pressure kiting.")]
    [SettingRange(0, 100, 5)]
    public int RangedKiteHpPercent { get; set; } = 75;

    [Setting("Combat", "Kite rare and unique enemies",
        "Ranged stance: after a short attack window against a nearby rare/unique, release the attack, dash away from surrounding threats, then resume firing.")]
    public bool RangedKiteToughTargets { get; set; } = true;

    [Setting("Combat", "Required map buff id",
        "Internal player-buff id that must be present before map clearing can act. Empty disables. RF builds use 'righteous_fire'. The bot verifies memory after every area transition and fails closed if the configured key cannot establish it.")]
    public string RequiredMapBuffName { get; set; } = "";

    [Setting("Combat", "Required map buff key",
        "Key used to enable the required map buff when it is absent. For the current RF build this is T.")]
    [SettingKeycode]
    public int RequiredMapBuffKey { get; set; } = 0;

    [Setting("Combat", "Required buff douse HP %",
        "If HP stays below this percent for several seconds while the required map buff is burning, toggle the buff OFF and recover (RF death-spiral guard). Two douses in one map abandon the map as unsustainable.")]
    [SettingRange(0, 90, 5)]
    public int RequiredMapBuffMinHpPercent { get; set; } = 30;

    [Setting("Combat", "Required buff re-light HP %",
        "Only re-activate the required map buff when HP is at or above this percent AND a real fight is nearby. Must sit well above the douse threshold: re-lighting at the douse line produces a burn-recover-burn loop that stalls the map (observed live: 28s cycles at 30%).")]
    [SettingRange(0, 100, 5)]
    public int RequiredMapBuffRelightHpPercent { get; set; } = 65;

    [Setting("Combat", "Proximity hold radius (grid)",
        "Proximity stance only: how close the bot gets to the nearest monster before stopping to let your aura kill it. Smaller = stands right on top of packs (tankier builds); make it roughly your aura/RF radius.")]
    [SettingRange(4, 40, 1)]
    public float ProximityHoldRadiusGrid { get; set; } = 14f;

    [Setting("Combat", "Proximity engage radius (grid)",
        "Proximity stance only: how far away a monster can be and still pull the bot over to engage it. Larger = roams further to hunt packs before exploring onward.")]
    [SettingRange(20, 150, 5)]
    public float ProximityEngageRadiusGrid { get; set; } = 75f;

    [Setting("Combat", "Proximity destination policy",
        "Proximity stance only: nearest hostile minimizes travel; rarity-weighted packs moves toward dense groups while strongly prioritizing rares and bosses.")]
    [SettingOptions("Nearest hostile", "0", "Rarity-weighted dense packs", "1")]
    public int ProximityDestinationPolicy { get; set; } = 1;

    [Setting("Combat", "Pack density radius (grid)",
        "Proximity stance only: hostiles within this radius of a candidate destination contribute to that pack's density score.")]
    [SettingRange(10, 50, 5)]
    public float ProximityDensityRadiusGrid { get; set; } = 25f;

    [Setting("Combat", "HP retreat threshold",
        "Bot stops attacking and retreats when HP fraction drops below this. 0 = disabled.")]
    [SettingRange(0, 1, 0.05)]
    public float HpRetreatThreshold { get; set; } = 0.35f;


    [Setting("Loot", "Loot filter",
        "All loot-filter knobs (per-category value thresholds, must-loot lists, quest-item skip). Expand to configure.")]
    [SettingNested]
    public LootSettings Loot { get; set; } = new();

    [Setting("Map modifiers", "Map modifier policy",
        "Build-specific map safety rules. Reflect and no-regeneration are never-run by default; all other entries are configurable.")]
    [SettingNested]
    public MapModifierSettings MapModifiers { get; set; } = new();

    // Retained for the non-production research scaffold; intentionally omitted from the
    // generated settings UI until Ultimatum becomes an approved production preset.
    public UltimatumSettings Ultimatum { get; set; } = new();

    [Setting("Exploration", "Frontier step (grid)",
        "How far ahead the bot picks an unexplored target. Smaller = more granular exploration; larger = bigger leaps between waypoints.")]
    [SettingRange(20, 200, 10)]
    public int FrontierStepGrid { get; set; } = 80;

    // Mechanic toggles (shrines, rituals + Favours shop, Eldritch altars, memory tears) moved
    // to per-mechanic blocks in the active farming strategy (Strategies tab).

    [Setting("Movement", "Arrival radius (grid)",
        "Stop walking when within this many grid cells of a non-interactable destination (frontier point, exploration target). Interactable targets use Interaction radius instead.")]
    [SettingRange(1, 20, 1)]
    public float ArrivalRadiusGrid { get; set; } = 4f;

    [Setting("Movement", "Interaction radius (grid)",
        "Single global radius for 'close enough to click/use'. Used by loot, chests, waypoints, NPCs, and area transitions. Live validation puts ordinary world interaction near 25 grid; terrain line-of-access is also required.")]
    [SettingRange(10, 100, 5)]
    public float InteractionRangeGrid { get; set; } = 25f;

    [Setting("Movement", "Allow gap crossing",
        "When on, A* may route through pf=0/targeting>0 cells (gaps) and the bot will fire a tagged Dash skill to cross them. Off = walk-around behavior only.")]
    public bool AllowGapCrossing { get; set; } = true;

    [Setting("Movement", "Pathfinding budget (nodes)",
        "Max A* nodes explored per pathfind. PoE maps run 1-3 million cells; 1M nodes covers a typical area corner-to-corner. Each node ≈ 1 µs; 1M = ~1 s worst-case. Pathfinds are debounced (every few seconds), so the cost is amortized.")]
    [SettingRange(100000, 5000000, 50000)]
    public int PathfindingMaxNodes { get; set; } = 1_000_000;

    // ── Overlay / campaign guidance ────────────────────────────────────────

    [Setting("Overlay", "Show campaign guidance",
        "Draw in-campaign guidance (route to the next area exit, in-area objectives, and points of interest) while in the manual overlay mode. Read-only — never drives movement.")]
    public bool ShowCampaignGuidance { get; set; } = true;

    [Setting("Overlay", "Show entity HP bars",
        "Draw a compact HP bar above every hostile monster in range (all rarities). Uniques also keep their full nameplate.")]
    public bool ShowEntityHpBars { get; set; } = true;

    [Setting("Overlay", "Show player marker on map",
        "Draw the cyan player blip at the center of the expanded-map overlay. Off by default — the map is already player-centered, so the marker is redundant and distracting.")]
    public bool ShowMapPlayerBlip { get; set; } = false;

    /// <summary>
    /// Looter's effective range — alias for <see cref="InteractionRangeGrid"/>. Kept as a
    /// derived property so existing call sites read clearly; never persisted to JSON since
    /// the underlying setting already is.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public float LootRangeGrid => Math.Max(InteractionRangeGrid, LootSearchRangeGrid);
}
