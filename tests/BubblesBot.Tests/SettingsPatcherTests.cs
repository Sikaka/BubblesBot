using System.Text.Json;
using BubblesBot.Bot.Settings;
using BubblesBot.Bot.Web;

namespace BubblesBot.Tests;

public sealed class SettingsPatcherTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static SettingsPatcher.PatchOp Op(string[] path, string valueJson)
    {
        using var doc = JsonDocument.Parse(valueJson);
        return new SettingsPatcher.PatchOp(path, doc.RootElement.Clone());
    }

    [Fact]
    public void AppliesTopLevelValue()
    {
        var settings = new BotSettings();
        var errors = SettingsPatcher.Apply(settings, [Op(["maxRunMinutes"], "30")], Json);
        Assert.Empty(errors);
        Assert.Equal(30, settings.MaxRunMinutes);
    }

    [Fact]
    public void AppliesNestedValueThroughSettingNested()
    {
        var settings = new BotSettings();
        var errors = SettingsPatcher.Apply(settings, [Op(["loot", "minChaosValue"], "12.5")], Json);
        Assert.Empty(errors);
        Assert.Equal(12.5f, settings.Loot.MinChaosValue);
    }

    [Fact]
    public void AppliesComplexValueLikeSkillProfile()
    {
        var settings = new BotSettings();
        var errors = SettingsPatcher.Apply(settings,
            [Op(["skills"], """{ "slots": [{ "name": "Walk", "vk": 32, "role": 1 }] }""")], Json);
        Assert.Empty(errors);
        var slot = Assert.Single(settings.Skills.Slots);
        Assert.Equal("Walk", slot.Name);
        Assert.Equal(32, slot.Vk);
    }

    [Fact]
    public void UnknownPathIsRejected()
    {
        var settings = new BotSettings();
        var errors = SettingsPatcher.Apply(settings, [Op(["noSuchSetting"], "1")], Json);
        var error = Assert.Single(errors);
        Assert.Contains("noSuchSetting", error.Message);
    }

    [Fact]
    public void NonAnnotatedPropertyIsNotWritable()
    {
        // Ultimatum is deliberately not [Setting]-annotated — the patcher must not reach it.
        var settings = new BotSettings();
        var errors = SettingsPatcher.Apply(settings, [Op(["ultimatum"], "{}")], Json);
        Assert.Single(errors);
    }

    [Fact]
    public void TypeMismatchIsRejectedWithoutPartialApply()
    {
        var settings = new BotSettings();
        var errors = SettingsPatcher.Apply(settings, [Op(["maxRunMinutes"], "\"not a number\"")], Json);
        Assert.Single(errors);
        Assert.Equal(0, settings.MaxRunMinutes);
    }

    [Fact]
    public void NullIntoValueTypeIsRejected()
    {
        var settings = new BotSettings();
        var errors = SettingsPatcher.Apply(settings, [Op(["botActive"], "null")], Json);
        Assert.Single(errors);
    }

    [Fact]
    public void MultipleOpsApplyInOrder()
    {
        var settings = new BotSettings();
        var errors = SettingsPatcher.Apply(settings,
            [Op(["botActive"], "true"), Op(["activeMode"], "4")], Json);
        Assert.Empty(errors);
        Assert.True(settings.BotActive);
        Assert.Equal(4, settings.ActiveMode);
    }
}
