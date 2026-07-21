using BubblesBot.Bot.Modes;
using BubblesBot.Bot.Strategies;

namespace BubblesBot.Tests;

public sealed class StrategyTemplatesTests
{
    /// <summary>Materialize a template the way CreateStrategy does before it is stored/validated.</summary>
    private static FarmingStrategy Materialize(StrategyTemplate template)
    {
        var doc = StrategySerialization.Clone(template.Doc);
        doc.Identity.Id = StrategyIdentity.NewId();
        if (string.IsNullOrWhiteSpace(doc.Identity.Name)) doc.Identity.Name = template.Name;
        return doc;
    }

    [Fact]
    public void TemplatesCarryNoIdentityIdButNameSet()
    {
        foreach (var template in StrategyTemplates.All())
        {
            Assert.False(string.IsNullOrWhiteSpace(template.Name));
            Assert.Empty(template.Doc.Identity.Id);   // a template is a starting point, not a stored doc
        }
    }

    [Fact]
    public void AllTemplatesValidateOnceMaterialized()
    {
        foreach (var template in StrategyTemplates.All())
        {
            var result = StrategyValidator.Validate(Materialize(template));
            Assert.True(result.Ok, $"{template.Name}: {string.Join("; ", result.Errors)}");
        }
    }

    [Fact]
    public void StackedDeckTemplateEnablesSmartAltarsAndCloisterRitual()
    {
        var template = StrategyTemplates.Find(StrategyTemplates.StackedDeckId);
        Assert.NotNull(template);
        var altars = template.Doc.Block<EldritchAltarsBlock>()!;
        Assert.True(altars.Enabled);
        Assert.Equal(AltarChoicePolicy.Smart, altars.Policy);
        var ritual = template.Doc.Block<RitualBlock>()!;
        Assert.Equal(RitualChainOrdering.CloisterCorpses, ritual.ChainOrdering);
        var scarab = Assert.Single(template.Doc.Supply.Scarabs);
        Assert.Equal(StackedDeckPolicy.CloisterScarabPathFragment, scarab.PathFragment);
    }

    [Fact]
    public void CustomTemplateHasAllMechanicsDisabled()
    {
        var template = StrategyTemplates.Find(StrategyTemplates.CustomId);
        Assert.NotNull(template);
        Assert.All(template.Doc.Mechanics, block => Assert.False(block.Enabled));
        Assert.True(StrategyValidator.Validate(Materialize(template)).Ok);
    }

    [Fact]
    public void UnknownTemplateReturnsNull()
    {
        Assert.Null(StrategyTemplates.Find("does-not-exist"));
    }

    [Fact]
    public void TemplateDocRoundTripsThroughSerialization()
    {
        foreach (var template in StrategyTemplates.All())
        {
            var json = StrategySerialization.Serialize(template.Doc);
            var parsed = StrategySerialization.Parse(json);
            Assert.Equal(template.Doc.Mechanics.Count, parsed.Mechanics.Count);
        }
    }
}
