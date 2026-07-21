using System.Text.Json;

namespace BubblesBot.Bot.Strategies;

/// <summary>
/// Throwaway capture of the pre-strategy map-farming settings that used to live on
/// <see cref="Settings.BotSettings"/>. Those properties were removed when map farming moved to
/// strategy documents; this DTO is deserialized from a raw config file (ignoring the many
/// unknown members) purely so the one-time seed migration can carry a user's live-validated
/// values into the two built-in strategies. Never persisted; never read at runtime.
///
/// <para>Defaults mirror the removed properties' defaults, so a fresh user (no config file, or a
/// file without these keys) seeds sensible built-ins.</para>
/// </summary>
public sealed class LegacyFarmSettings
{
    public int MapFarmPreset { get; set; }
    public string MapFarmSupplyTabName { get; set; } = "Supplies";
    public string MapFarmDumpTabName { get; set; } = "Dump";
    public string MapFarmTargetMapName { get; set; } = "City Square";
    public int StackedDeckCloisterScarabsPerMap { get; set; } = 5;
    public int StackedDeckTargetMaps { get; set; } = 20;
    public bool StackedDeckDepositToStash { get; set; } = true;
    public bool TakeShrines { get; set; } = true;
    public bool TakeRituals { get; set; } = true;
    public bool DeferRitualsUntilMapSweep { get; set; } = true;
    public bool BuyRitualRewards { get; set; } = true;
    public float RitualRerollThresholdChaos { get; set; } = 15f;
    public float RitualFinalBuyMinChaos { get; set; } = 5f;
    public int RitualMaxRerolls { get; set; } = 10;
    public bool TakeMemoryTears { get; set; } = true;
    public int AltarPolicy { get; set; }
    public int ExplorationDonePercent { get; set; } = 85;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Read the legacy values from a raw config JSON file. Missing file / unreadable / missing
    /// keys all fall back to defaults — the seed is best-effort, never a hard dependency.
    /// </summary>
    public static LegacyFarmSettings LoadFrom(string? configPath)
    {
        if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath)) return new LegacyFarmSettings();
        try
        {
            return JsonSerializer.Deserialize<LegacyFarmSettings>(File.ReadAllText(configPath), JsonOptions)
                ?? new LegacyFarmSettings();
        }
        catch
        {
            return new LegacyFarmSettings();
        }
    }
}
