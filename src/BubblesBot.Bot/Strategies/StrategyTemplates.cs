namespace BubblesBot.Bot.Strategies;

/// <summary>One selectable archetype the "create strategy" flow can start from.</summary>
public sealed record StrategyTemplate(string TemplateId, string Name, string Description, int Mode, FarmingStrategy Doc);

/// <summary>
/// Built-in archetypes for creating a fresh strategy. Distinct from
/// <see cref="LegacySettingsMigration"/> (which seeds from the user's live settings): these
/// are clean, settings-independent starting points built from <see cref="BotSettings"/>
/// defaults. Blight and Simulacrum are intentionally NOT here — they remain legacy
/// settings-configured modes in this build; the wizard steers them to their settings pages.
/// </summary>
public static class StrategyTemplates
{
    public const string GeneralMappingId = "general";
    public const string StackedDeckId = "stackedDeck";
    public const string CustomId = "custom";

    public static IReadOnlyList<StrategyTemplate> All()
    {
        var defaults = new LegacyFarmSettings();
        return
        [
            new StrategyTemplate(
                GeneralMappingId,
                LegacySettingsMigration.GeneralMappingName,
                "Run a pre-rolled map with the shared clear loop and configured mechanics. No scarab recipe.",
                4,
                Fresh(LegacySettingsMigration.GeneralMapping(defaults))),
            new StrategyTemplate(
                StackedDeckId,
                LegacySettingsMigration.CloisterStackedDecksName,
                "Cloister scarabs + corpse-ordered Ritual chain for stacked decks. Deposits after every map.",
                4,
                Fresh(EnableSmartAltars(LegacySettingsMigration.CloisterStackedDecks(defaults)))),
            new StrategyTemplate(
                CustomId,
                "Custom",
                "Start from an empty map-farming strategy and choose every mechanic yourself.",
                4,
                Fresh(Empty())),
        ];
    }

    public static StrategyTemplate? Find(string templateId)
        => All().FirstOrDefault(t => string.Equals(t.TemplateId, templateId, StringComparison.OrdinalIgnoreCase));

    /// <summary>A blank map-farming strategy: supply/prep defaults, all mechanics present but disabled.</summary>
    private static FarmingStrategy Empty()
    {
        var strategy = LegacySettingsMigration.GeneralMapping(new LegacyFarmSettings());
        strategy.Identity.Description = "";
        foreach (var block in strategy.Mechanics) block.Enabled = false;
        return strategy;
    }

    private static FarmingStrategy EnableSmartAltars(FarmingStrategy strategy)
    {
        if (strategy.Block<EldritchAltarsBlock>() is { } altars)
        {
            altars.Enabled = true;
            altars.Policy = AltarChoicePolicy.Smart;
        }
        return strategy;
    }

    /// <summary>Reset identity so a template is a clean starting point, not a stored strategy.</summary>
    private static FarmingStrategy Fresh(FarmingStrategy strategy)
    {
        strategy.Identity = new StrategyIdentity
        {
            Id = "",
            Name = strategy.Identity.Name,
            Description = strategy.Identity.Description,
            Author = "",
            GameVersion = "",
        };
        return strategy;
    }
}
