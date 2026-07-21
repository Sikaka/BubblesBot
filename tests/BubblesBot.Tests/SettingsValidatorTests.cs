using BubblesBot.Bot.Settings;
using BubblesBot.Bot.Web;

namespace BubblesBot.Tests;

public sealed class SettingsValidatorTests
{
    [Fact]
    public void DefaultSettingsValidateClean()
    {
        Assert.Empty(SettingsValidator.Validate(new BotSettings()));
    }

    [Fact]
    public void RangeViolationIsRejectedWithPath()
    {
        var settings = new BotSettings { MaxRunMinutes = 99999 };   // range 0..1440
        var errors = SettingsValidator.Validate(settings);
        var error = Assert.Single(errors);
        Assert.Equal("maxRunMinutes", error.Path);
        Assert.Contains("between", error.Message);
    }

    [Fact]
    public void OptionsMembershipIsEnforced()
    {
        var settings = new BotSettings { ActiveMode = 3 };   // legal: 0, 4, 5, 6
        var errors = SettingsValidator.Validate(settings);
        Assert.Contains(errors, e => e.Path == "activeMode");
    }

    [Fact]
    public void KeycodeRangeIsEnforced()
    {
        var settings = new BotSettings { SimulacrumInventoryKeyVk = 999 };
        var errors = SettingsValidator.Validate(settings);
        Assert.Contains(errors, e => e.Path == "simulacrumInventoryKeyVk" && e.Message.Contains("0..255"));
    }

    [Fact]
    public void NestedSettingsAreValidatedWithDottedPaths()
    {
        var settings = new BotSettings();
        settings.Loot.MinChaosValue = 5000f;   // range 0..100
        var errors = SettingsValidator.Validate(settings);
        Assert.Contains(errors, e => e.Path == "loot.minChaosValue");
    }

    [Fact]
    public void FloatRangeBoundariesAreInclusive()
    {
        var settings = new BotSettings { HpRetreatThreshold = 1f };   // range 0..1
        Assert.DoesNotContain(SettingsValidator.Validate(settings), e => e.Path == "hpRetreatThreshold");
    }
}
