using System.Text.RegularExpressions;
using BubblesBot.Core.Knowledge;

namespace BubblesBot.Tests;

public sealed class MapModifierCatalogTests
{
    [Fact]
    public void SemanticKeysAreUniqueAndCatalogIsBroad()
    {
        Assert.True(MapModifierCatalog.Known.Count >= 75);
        Assert.Equal(MapModifierCatalog.Known.Count,
            MapModifierCatalog.Known.Select(x => x.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void EverySeedTemplateMatchesARepresentativeLiveRoll()
    {
        foreach (var definition in MapModifierCatalog.Known)
        {
            var lines = definition.TooltipTemplates.Select(RepresentativeRoll).ToArray();
            var verdict = MapModifierCatalog.EvaluateTooltip(lines);
            Assert.Contains(verdict.Matches, x => x.Definition.Key == definition.Key);
        }
    }

    [Fact]
    public void CurrentBuildDefaultsVetoOnlyReflectAndNoRegeneration()
    {
        var never = MapModifierCatalog.Known
            .Where(x => x.DefaultDisposition == MapModifierDisposition.Never)
            .Select(x => x.Key).Order().ToArray();
        Assert.Equal([
            "recovery.no-regeneration",
            "reflect.elemental",
            "reflect.physical",
        ], never);
    }

    [Fact]
    public void CompoundLiveModifiersResolveToSemanticKeys()
    {
        var verdict = MapModifierCatalog.EvaluateTooltip([
            "Monsters have 365% increased Critical Strike Chance",
            "+44% to Monster Critical Strike Multiplier",
            "Monsters deal 104% extra Physical Damage as Lightning",
            "Players cannot Regenerate Life, Mana or Energy Shield",
        ]);

        Assert.Contains(verdict.Matches, x => x.Definition.Key == "monster.critical-strikes");
        Assert.Contains(verdict.Matches, x => x.Definition.Key == "monster.extra-lightning");
        Assert.Contains(verdict.Never, x => x.Definition.Key == "recovery.no-regeneration");
        Assert.False(verdict.Runnable);
    }

    [Fact]
    public void BuildOverridesReplaceCatalogDefaults()
    {
        var verdict = MapModifierCatalog.EvaluateTooltip([
            "Monsters reflect 18% of Physical Damage",
            "Monsters deal 98% extra Physical Damage as Fire",
        ], [
            "reflect.physical=0",
            "monster.extra-fire=2",
        ]);

        Assert.DoesNotContain(verdict.Never, x => x.Definition.Key == "reflect.physical");
        Assert.Contains(verdict.Never, x => x.Definition.Key == "monster.extra-fire");
    }

    [Fact]
    public void AdvancedTooltipRollAndTierRangeMatchesTemplate()
    {
        var verdict = MapModifierCatalog.EvaluateTooltip([
            "Monsters have 365(360-400)% increased Critical Strike Chance",
            "+44(41-45)% to Monster Critical Strike Multiplier",
        ]);
        Assert.Contains(verdict.Matches, x => x.Definition.Key == "monster.critical-strikes");
    }

    [Theory]
    [InlineData("unknown.key=2")]
    [InlineData("reflect.physical=9")]
    [InlineData("reflect.physical")]
    public void InvalidOverridesAreRejected(string row)
        => Assert.NotNull(MapModifierCatalog.ValidateOverrides([row]));

    private static string RepresentativeRoll(string template)
        => Regex.Replace(template, @"\((\d+)-(\d+)\)", m => m.Groups[1].Value);
}
