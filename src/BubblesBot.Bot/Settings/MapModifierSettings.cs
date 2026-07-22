namespace BubblesBot.Bot.Settings;

/// <summary>Per-character/build map-modifier policy keyed by semantic catalog identifiers.</summary>
public sealed class MapModifierSettings
{
    [Setting("Map modifiers", "Build map-modifier policy",
        "Allow, avoid, or never run each known map modifier. Only changes from the catalog defaults are stored in the profile.")]
    [SettingMapModifierTable]
    public List<string> PolicyOverrides { get; set; } = new();
}
