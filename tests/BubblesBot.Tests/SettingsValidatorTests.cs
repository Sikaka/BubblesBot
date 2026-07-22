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
        var settings = new BotSettings { ActiveMode = 3 };   // legal: 0, 4, 5, 6, 7
        var errors = SettingsValidator.Validate(settings);
        Assert.Contains(errors, e => e.Path == "activeMode");
    }

    [Fact]
    public void GuardianRotaModeIsAValidOption()
        => Assert.DoesNotContain(
            SettingsValidator.Validate(new BotSettings { ActiveMode = 7 }),
            error => error.Path == "activeMode");

    [Fact]
    public void GuardianPreferredQuantityCannotBeBelowMinimum()
    {
        var settings = new BotSettings
        {
            GuardianInvitationMinQuantity = 75,
            GuardianInvitationPreferredQuantity = 65,
        };

        Assert.Contains(SettingsValidator.Validate(settings), error =>
            error.Path == "guardianInvitationPreferredQuantity");
    }

    [Fact]
    public void GuardianDumpTabCannotBeBlank()
    {
        var settings = new BotSettings { GuardianDumpTabName = "  " };

        Assert.Contains(SettingsValidator.Validate(settings), error =>
            error.Path == "guardianDumpTabName");
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

    [Fact]
    public void MapModifierPolicyRejectsUnknownSemanticKey()
    {
        var settings = new BotSettings();
        settings.MapModifiers.PolicyOverrides.Add("not-a-real-map-mod=2");
        var errors = SettingsValidator.Validate(settings);
        Assert.Contains(errors, e => e.Path == "mapModifiers.policyOverrides"
            && e.Message.Contains("unknown", StringComparison.OrdinalIgnoreCase));
    }
}
