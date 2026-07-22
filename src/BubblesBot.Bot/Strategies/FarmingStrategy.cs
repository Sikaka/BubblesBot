using System.Text.Json.Serialization;
using BubblesBot.Bot.Modes;
using BubblesBot.Bot.Settings;

namespace BubblesBot.Bot.Strategies;

/// <summary>
/// A named, shareable farming-strategy document — the artifact the setup wizard produces and
/// the map-farming runtime executes. Strategies are pure data over built-in mechanic
/// implementations: the mechanic list is a closed polymorphic set, unknown mechanic types and
/// unknown fields fail deserialization outright, and anything the current build cannot execute
/// fails validation instead of being silently accepted.
///
/// <para>What is deliberately NOT here: skills, flasks, keybinds, combat tuning, loot-filter
/// details — anything describing a build or this machine lives in the per-character
/// <see cref="BotSettings"/> profile. This document describes only the farming routine, so a
/// file exported by one user drops cleanly onto another user's build.</para>
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class FarmingStrategy
{
    /// <summary>Format version. Gate + migration pipeline live in <see cref="StrategyMigrations"/>.</summary>
    public int SchemaVersion { get; set; } = StrategyMigrations.CurrentSchemaVersion;

    public StrategyIdentity Identity { get; set; } = new();
    public SupplySection Supply { get; set; } = new();
    public MapPrepSection MapPrep { get; set; } = new();
    public List<MechanicBlock> Mechanics { get; set; } = new();
    public LootStrategySection Loot { get; set; } = new();
    public CompletionSection Completion { get; set; } = new();
    public CampaignSection Campaign { get; set; } = new();
    public LimitsSection Limits { get; set; } = new();

    /// <summary>First block of the given type, or null. Mirrors SkillProfile's role queries.</summary>
    public T? Block<T>() where T : MechanicBlock => Mechanics.OfType<T>().FirstOrDefault();

    /// <summary>True when a block of the given type exists and is enabled.</summary>
    public bool IsEnabled<T>() where T : MechanicBlock => Block<T>() is { Enabled: true };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class StrategyIdentity
{
    /// <summary>32-hex id; doubles as the on-disk filename so display names can be anything.</summary>
    public string Id { get; set; } = NewId();

    [Setting("Identity", "Name", "Display name shown in the strategy list and run telemetry.")]
    public string Name { get; set; } = "";

    [Setting("Identity", "Description", "What this strategy farms and what it assumes (scarabs, atlas setup, influence).")]
    public string Description { get; set; } = "";

    public string Author { get; set; } = "";

    /// <summary>PoE patch the author validated against — informational, never a gate.</summary>
    public string GameVersion { get; set; } = "";

    public DateTime CreatedUtc { get; set; }
    public DateTime ModifiedUtc { get; set; }

    public static string NewId() => Guid.NewGuid().ToString("N");
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SupplySection
{
    [Setting("Supplies", "Supply tab name",
        "Named stash tab holding pre-rolled maps and scarabs for the typed withdrawal contract. Verified in-game at runtime, never at save time.")]
    public string SuppliesTabName { get; set; } = "Supplies";

    [Setting("Supplies", "Dump tab name",
        "Regular, Premium, or Quad stash tab that receives loot when depositing between maps.")]
    public string DumpTabName { get; set; } = "Dump";

    public MapSupply Map { get; set; } = new();

    /// <summary>Scarab loadout per map. Slot-occupancy proof iterates these lines; the device has five scarab slots total.</summary>
    public List<ScarabLine> Scarabs { get; set; } = new();

    public List<CurrencyReserve> CurrencyReserves { get; set; } = new();
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MapSupply
{
    public MapSource Source { get; set; } = MapSource.AtlasStorage;

    [Setting("Supplies", "Target map name",
        "Map consumed each run. Doubles as the consume-safety check against the atlas panel's selected node name.")]
    public string TargetMapName { get; set; } = "City Square";

    /// <summary>When true, replenish generic map keys from SuppliesTabName before device use.</summary>
    public bool RestockFromStash { get; set; }

    [SettingRange(1, 20, 1)]
    public int CarriedMapBuffer { get; set; } = 1;
}

/// <summary>Where the next map comes from. Only atlas storage is live-proven; stash withdrawal is a future contract.</summary>
public enum MapSource { AtlasStorage, PlayerInventory, StashTab }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ScarabLine
{
    /// <summary>Metadata-path fragment — the only identity the read side can positively verify.</summary>
    public string PathFragment { get; set; } = "";

    /// <summary>Human label for the UI; never used for matching.</summary>
    public string DisplayName { get; set; } = "";

    [SettingRange(0, 5, 1)]
    public int CountPerMap { get; set; } = 1;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CurrencyReserve
{
    public string Item { get; set; } = "PortalScroll";
    // Separate-arena boss maps consume one checkpoint scroll and one successful-exit scroll.
    public int MinCount { get; set; } = 2;
    public ReservePolicy Policy { get; set; } = ReservePolicy.RetainFullestStack;
}

public enum ReservePolicy { RetainFullestStack }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MapPrepSection
{
    [Setting("Map prep", "Atlas node",
        "Atlas node clicked to stage the map. Node → click-coordinate data is a per-patch built-in catalog; unsupported names fail closed at the device.")]
    public string AtlasNodeName { get; set; } = "City Square";

    public MapRollingSection Rolling { get; set; } = new();
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MapRollingSection
{
    public MapRollingMode Mode { get; set; } = MapRollingMode.None;

    /// <summary>
    /// Flattened Stats.dat ids that make a rolled map unacceptable to this build. Numeric
    /// identity keeps the strategy locale-independent and matches the map-item read side.
    /// </summary>
    public List<int> RejectedStatIds { get; set; } = new();

    [SettingRange(1, 100, 1)]
    public int MaxAttempts { get; set; } = 20;
}

/// <summary>
/// Desired map preparation. Scoured is executable for inventory-fed generic map keys because
/// the supply selector positively requires Normal rarity. Rare currency loops are represented
/// now but remain activation-blocked until the currency application transaction is live-proven.
/// </summary>
public enum MapRollingMode { None, Scoured, Rare, RareCorrupted }

/// <summary>
/// One mechanic building block. The discriminated set is closed and in-repo — adding a mechanic
/// means adding a derived type, an adapter, and tests; imported files naming an unknown type
/// fail at parse time. <see cref="SweepBias"/> handicaps the nearest-wins sweep arbitration in
/// grid units (positive = treated as closer than it is), bounded so it biases ties rather than
/// re-ordering phases.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(ShrinesBlock), ShrinesBlock.TypeId)]
[JsonDerivedType(typeof(EldritchAltarsBlock), EldritchAltarsBlock.TypeId)]
[JsonDerivedType(typeof(RitualBlock), RitualBlock.TypeId)]
[JsonDerivedType(typeof(MemoryTearsBlock), MemoryTearsBlock.TypeId)]
[JsonDerivedType(typeof(DeliriumBlock), DeliriumBlock.TypeId)]
[JsonDerivedType(typeof(UltimatumBlock), UltimatumBlock.TypeId)]
[JsonDerivedType(typeof(StrongboxesBlock), StrongboxesBlock.TypeId)]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public abstract class MechanicBlock
{
    [Setting("Mechanics", "Enabled", "Whether this mechanic composes into the run at all.")]
    public bool Enabled { get; set; } = true;

    [Setting("Mechanics", "Sweep bias (grid)",
        "Distance handicap in the nearest-wins sweep arbitration. Positive values make this mechanic win roughly-equidistant ties against loot and other mechanics.")]
    [SettingRange(-100, 100, 5)]
    public float SweepBias { get; set; }

    /// <summary>The JSON discriminator, exposed for registry/validation lookups.</summary>
    [JsonIgnore]
    public abstract string MechanicId { get; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ShrinesBlock : MechanicBlock
{
    public const string TypeId = "shrines";
    public override string MechanicId => TypeId;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class EldritchAltarsBlock : MechanicBlock
{
    public const string TypeId = "eldritchAltars";
    public override string MechanicId => TypeId;

    [Setting("Eldritch altars", "Choice policy",
        "Skip never clicks. Top/Bottom are literal. Smart ranks by reward weight with hard vetoes; it stays fail-closed until the option UI is live-proven for the current build.")]
    public AltarChoicePolicy Policy { get; set; } = AltarChoicePolicy.Skip;

    [Setting("Eldritch altars", "Defer choices until boss dead",
        "Hold every altar choice until map-boss death is positively confirmed so later choices cannot roll boss-drop options. Requires boss-kill evidence support.")]
    public bool DeferChoicesUntilBossDead { get; set; }

    /// <summary>
    /// Per-strategy scoring overrides keyed by <see cref="EldritchAltarScoring.Normalize"/>
    /// output (letters only, tags stripped). The UI renders friendly mod text; the file stores
    /// normalized keys so matching is deterministic.
    /// </summary>
    public Dictionary<string, int> WeightOverrides { get; set; } = new();
}

/// <summary>Order matches the legacy AltarPolicy setting ints (0..3) for migration fidelity.</summary>
public enum AltarChoicePolicy { Skip, Top, Bottom, Smart }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RitualBlock : MechanicBlock
{
    public const string TypeId = "ritual";
    public override string MechanicId => TypeId;

    [Setting("Ritual", "Defer until map sweep",
        "Remember altars during exploration and run the chain after the sweep, maximizing the resurrection pool. Active rituals are always resumed immediately.")]
    public bool DeferUntilMapSweep { get; set; } = true;

    [Setting("Ritual", "Chain ordering",
        "Nearest-first minimizes travel. Corpse-count ordering runs the altar with the most tracked corpses first because completed pools repeat in later rituals.")]
    public RitualChainOrdering ChainOrdering { get; set; } = RitualChainOrdering.NearestFirst;

    /// <summary>Monster metadata fragment counted for corpse ordering (e.g. "DemonFemale" for Cloister students).</summary>
    public string CorpseMonsterPathFragment { get; set; } = "";

    [SettingRange(5, 120, 5)]
    public float CorpseRadiusGrid { get; set; } = StackedDeckPolicy.RitualCorpseRadiusGrid;

    /// <summary>Strategy-weight bonus for packs of the tracked corpse monster during proximity destination scoring.</summary>
    [SettingRange(0, 100, 1)]
    public double DensityWeight { get; set; }

    public RitualShopBlock Shop { get; set; } = new();
}

public enum RitualChainOrdering { NearestFirst, CloisterCorpses }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RitualShopBlock
{
    [Setting("Ritual", "Buy rewards", "After the chain, open Favours, buy valuable offers, reroll, and spend remaining tribute.")]
    public bool Enabled { get; set; } = true;

    [Setting("Ritual", "Reroll threshold (chaos)", "While rerolls remain, buy offers worth at least this much before rerolling.")]
    [SettingRange(0, 500, 1)]
    public float RerollThresholdChaos { get; set; } = 15f;

    [Setting("Ritual", "Final buy minimum (chaos)", "After rerolls, spend remaining tribute on offers at or above this stack value.")]
    [SettingRange(0, 100, 0.5)]
    public float FinalBuyMinChaos { get; set; } = 5f;

    [Setting("Ritual", "Maximum rerolls", "Safety cap per map; the Favours UI's own reroll count is also respected.")]
    [SettingRange(0, 20, 1)]
    public int MaxRerolls { get; set; } = 10;
}

/// <summary>Atlas Memory tears: click them during the sweep; the normal loot pass collects the drop. No policy.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MemoryTearsBlock : MechanicBlock
{
    public const string TypeId = "memoryTears";
    public override string MechanicId => TypeId;
}

/// <summary>A Delirium mirror layered over the ordinary clear, loot, boss, and mechanic loop.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DeliriumBlock : MechanicBlock
{
    public const string TypeId = "delirium";
    public override string MechanicId => TypeId;

    [Setting("Delirium", "Initial fog lead (grid)",
        "Forward distance allowed immediately after crossing before the front-pacing hold can engage.")]
    [SettingRange(10, 250, 5)]
    public float InitialFogLeadGrid { get; set; } = 60;

    [Setting("Delirium", "Maximum forward pace (grid/s)",
        "Average radial progress ceiling from the mirror. Fast characters pause when they outrun the expanding fog front.")]
    [SettingRange(1, 100, 1)]
    public float MaxForwardGridPerSecond { get; set; } = 12;

    [Setting("Delirium", "Maximum pack dwell (seconds)",
        "For proximity/RF builds, stop in a dense pack only this long before resuming forward fog progress.")]
    [SettingRange(1, 15, 0.5)]
    public double MaximumPackDwellSeconds { get; set; } = 3.5;

    [SettingRange(5, 60, 1)]
    public int EndButtonTimeoutSeconds { get; set; } = 15;

    [Setting("Delirium", "Minimum reward wait (seconds)",
        "Hold the map after the end button disappears so delayed reward piles can materialise.")]
    [SettingRange(5, 120, 5)]
    public int MinimumRewardWaitSeconds { get; set; } = 40;

    [SettingRange(1, 15, 0.5)]
    public double RewardQuietSeconds { get; set; } = 3;

    [SettingRange(30, 180, 5)]
    public int MaximumRewardWaitSeconds { get; set; } = 120;
}

/// <summary>
/// Rush the map-wide Ultimatum altar tile, run the supported encounter types, then optionally
/// end the map immediately. Modifier danger and RF/Cast-on-Stunned positioning are character
/// concerns and remain in <see cref="BotSettings.Ultimatum"/>; this block only describes the
/// farming routine.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class UltimatumBlock : MechanicBlock
{
    public const string TypeId = "ultimatum";
    public override string MechanicId => TypeId;

    [Setting("Ultimatum", "Exit after encounter",
        "Leave the map as soon as Ultimatum completes or is skipped. This is the core rush-and-reset farming loop.")]
    public bool ExitAfter { get; set; } = true;

    public bool DoSurvive { get; set; } = true;
    public bool DoKillEnemies { get; set; } = true;
    public bool DoDefendAltar { get; set; } = true;

    [Setting("Ultimatum", "Do stone circles",
        "Stand in the Stone Circles needs positional capture logic. Keep this off to skip the encounter and immediately leave the map.")]
    public bool DoStandInCircles { get; set; }
}

/// <summary>Schema stub — parses and renders, but no adapter exists yet, so enabling it fails validation.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class StrongboxesBlock : MechanicBlock
{
    public const string TypeId = "strongboxes";
    public override string MechanicId => TypeId;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class LootStrategySection
{
    /// <summary>
    /// Overrides the profile's bounded mechanic-cleanup memory threshold for this strategy.
    /// Null inherits the profile value; 0 remembers every accepted local drop. This never
    /// authorizes a whole-map completion/backtracking pass.
    /// </summary>
    [SettingRange(0, 1000, 1)]
    public float? BacktrackMinChaosOverride { get; set; }

    [Setting("Loot", "Deposit after each map", "Walk to the stash and deposit loot between maps instead of running until the inventory stop.")]
    public bool DepositAfterEachMap { get; set; } = true;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CompletionSection
{
    [Setting("Completion", "Require boss kill",
        "Positive map-boss death evidence gates map completion. Requires a boss catalog entry for the target map.")]
    public bool RequireBossKill { get; set; }

    public MultiAreaPolicy MultiArea { get; set; } = MultiAreaPolicy.ExhaustAllZones;

    [Setting("Completion", "Target map count", "Stop the loop after this many completed maps. Supplies or portal scrolls running out also stops it.")]
    [SettingRange(1, 500, 1)]
    public int TargetMaps { get; set; } = 20;

    [Setting("Completion", "Exploration done %",
        "Reveal percentage at which the map counts as swept: rituals start and the zone can finish. Map farming is mechanics-first, so grinding the last few percent of reveal wastes time. 100 = only stop when the frontier is truly exhausted.")]
    [SettingRange(50, 100, 5)]
    public int ExplorationDonePercent { get; set; } = 85;
}

public enum MultiAreaPolicy { ExhaustAllZones }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CampaignSection
{
    /// <summary>Multi-map campaign rules (e.g. guardian rotations). Reserved: only None validates until execution ships.</summary>
    public CampaignMode Mode { get; set; } = CampaignMode.None;
}

public enum CampaignMode { None, GuardianRotation }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class LimitsSection
{
    [Setting("Limits", "Max map time (minutes)",
        "Hard deadline for one whole map attempt, including boss arenas and subzones. At the limit the map is abandoned through a town portal, is not credited, and the loop starts a new map. 0 disables it.")]
    [SettingRange(0, 60, 1)]
    public int MaxMapMinutes { get; set; }

    /// <summary>Per-zone stuck failsafe in minutes. Null inherits the profile value; 0 disables the failsafe.</summary>
    [SettingRange(0, 60, 1)]
    public int? MaxZoneMinutes { get; set; }

    [Setting("Limits", "Max mechanic stalls per map",
        "How many bounded interaction retries a single mechanic may burn before the run abandons it and emits an incident.")]
    [SettingRange(0, 20, 1)]
    public int MaxMechanicStallsPerMap { get; set; } = 3;
}
