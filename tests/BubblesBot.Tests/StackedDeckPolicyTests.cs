using BubblesBot.Bot.Modes;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Tests;

public sealed class StackedDeckPolicyTests
{
    [Theory]
    [InlineData("Metadata/Items/Scarabs/ScarabDivinationCardsNew1", true)]
    [InlineData("Metadata/Items/Scarabs/ScarabDivinationCardsNew2", false)]
    public void CloisterScarabUsesProvenMetadataIdentity(string path, bool expected)
        => Assert.Equal(expected, StackedDeckPolicy.IsCloisterScarab(path));

    [Theory]
    [InlineData("Metadata/Monsters/DemonModular/DemonFemaleStudent", true)]
    [InlineData("Metadata/Monsters/DemonFemale/DemonFemale2", true)]
    [InlineData("Metadata/Monsters/DemonMale/DemonMale2", false)]
    public void CloisterMonsterUsesProvenMetadataIdentity(string path, bool expected)
        => Assert.Equal(expected, StackedDeckPolicy.IsCloisterMonster(path));

    [Fact]
    public void RichestRitualWinsBeforeTravelDistance()
    {
        var near = Ritual(1, 5, 0);
        var rich = Ritual(2, 100, 0);
        var scores = new Dictionary<uint, int> { [near.Id] = 2, [rich.Id] = 9 };

        var chosen = StackedDeckPolicy.ChooseRitual(
            [near, rich], Point(0, 0), id => scores[id]);

        Assert.Equal(rich.Id, chosen!.Id);
    }

    [Fact]
    public void RitualTravelOnlyBreaksEqualCorpseScores()
    {
        var near = Ritual(1, 5, 0);
        var far = Ritual(2, 100, 0);

        var chosen = StackedDeckPolicy.ChooseRitual(
            [far, near], Point(0, 0), _ => 4);

        Assert.Equal(near.Id, chosen!.Id);
    }

    [Fact]
    public void StackedDeckAltarScoringPrefersCurrencyDuplicationAndRejectsDeadlyRisk()
    {
        var profitable = EldritchAltarScoring.ScoreChoice(
            "Player gains:\n<valuedefault>Basic Currency Items dropped by slain Enemies have 35% chance to be Duplicated");
        var deadly = EldritchAltarScoring.ScoreChoice(
            "Player gains:\nTake 600 Chaos Damage per second during any Flask Effect");

        Assert.Equal(95, profitable);
        Assert.Equal(-500, deadly);
        Assert.True(profitable > deadly);
    }

    [Fact]
    public void ModifierNormalizationIgnoresNumbersAndMarkup()
    {
        Assert.Equal(
            EldritchAltarScoring.Normalize("#% increased Quantity of Items found in this Area"),
            EldritchAltarScoring.Normalize("<valuedefault>37% increased Quantity of Items found in this Area"));
    }

    // Live-captured 2026-07-15 via mechanic.eldritch-altar-ui (TangleAltar, entityId 6286).
    // Markup tags re-added around mod lines to also exercise Normalize's tag stripping —
    // production reads TextNoTags, which is tag-free.
    private const string LiveTopChoice =
        "Player gains:\n<enchanted>-53% to Cold Resistance</enchanted>\n<enchanted>-46% to Lightning Resistance</enchanted>\n" +
        "<enchanted>Non-Damaging Ailments you inflict are reflected back to you</enchanted>\n" +
        "<enchanted>25% increased Quantity of Items found in this Area</enchanted>";
    private const string LiveBottomChoice =
        "Eldritch Minions gain:\n<enchanted>Inflict 1 Grasping Vine on Hit</enchanted>\n" +
        "<enchanted>68% additional Physical Damage Reduction</enchanted>\n" +
        "<enchanted>3.3% chance to drop an additional Orb of Alteration</enchanted>";

    [Fact]
    public void SmartAltarPolicyTakesQuantityOverMinorCurrencyDespiteResDownsides()
    {
        var verdict = EldritchAltarScoring.Decide(3, LiveTopChoice, LiveBottomChoice);

        Assert.Equal(EldritchAltarScoring.AltarDecision.Top, verdict.Decision);
        Assert.Equal(90, verdict.Top.Reward);       // quantity line only
        Assert.False(verdict.Top.Vetoed);           // res penalties are not deadly
        Assert.Equal(5, verdict.Bottom.Reward);     // alteration orb
    }

    [Fact]
    public void SmartAltarPolicyVetoesDeadlyLineEvenWithBigReward()
    {
        var deadly = "Player gains:\nTake 600 Chaos Damage per second during any Flask Effect\n" +
                     "50% chance to drop an additional Divine Orb";
        var mild = "Eldritch Minions gain:\n15% increased Movement Speed\n" +
                   "3% chance to drop an additional Orb of Alteration";

        var verdict = EldritchAltarScoring.Decide(3, deadly, mild);

        Assert.True(verdict.Top.Vetoed);
        Assert.Equal(EldritchAltarScoring.AltarDecision.Bottom, verdict.Decision);
    }

    [Theory]
    // Wording variants that do NOT exactly match the weight table must still veto —
    // an unmatched variant scoring 0 killed a live RF character on 2026-07-15.
    [InlineData("Player gains:\n40% reduced Recovery Rate of Life and Energy Shield per Endurance Charge")]
    [InlineData("Player gains:\n25% reduced Recovery Rate of Energy Shield")]
    [InlineData("Player gains:\nTake 450 Chaos Damage per second during any Flask Effect\nextra line")]
    public void KillClassModVariantsAreVetoedBySubstring(string choiceText)
    {
        var verdict = EldritchAltarScoring.Decide(3, choiceText,
            "Eldritch Minions gain:\n15% increased Movement Speed");

        Assert.True(verdict.Top.Vetoed);
        Assert.Equal(EldritchAltarScoring.AltarDecision.Bottom, verdict.Decision);
    }

    [Fact]
    public void SmartAltarPolicySkipsWhenBothChoicesAreVetoed()
    {
        var deadlyA = "Player gains:\nTake 600 Chaos Damage per second during any Flask Effect";
        var deadlyB = "Player gains:\n30% reduced Recovery Rate of Life, Mana and Energy Shield per Endurance Charge";

        var verdict = EldritchAltarScoring.Decide(3, deadlyA, deadlyB);

        Assert.Equal(EldritchAltarScoring.AltarDecision.Skip, verdict.Decision);
    }

    [Theory]
    [InlineData(0, EldritchAltarScoring.AltarDecision.Skip)]
    [InlineData(1, EldritchAltarScoring.AltarDecision.Top)]
    [InlineData(2, EldritchAltarScoring.AltarDecision.Bottom)]
    public void FixedAltarPoliciesAreLiteral(int policy, EldritchAltarScoring.AltarDecision expected)
        => Assert.Equal(expected,
            EldritchAltarScoring.Decide(policy, LiveTopChoice, LiveBottomChoice).Decision);

    [Fact]
    public void UnrecognizedAltarModLinesSurfaceForWeightTableGrowth()
    {
        var analysis = EldritchAltarScoring.Analyze(
            "Player gains:\nSome brand new league mod nobody has weighted yet\n" +
            "25% increased Quantity of Items found in this Area");

        Assert.Equal(90, analysis.Reward);
        Assert.Single(analysis.UnknownLines);
        Assert.Contains("brand new league mod", analysis.UnknownLines[0]);
    }

    private static MechanicEntry Ritual(uint id, int x, int y)
    {
        var entry = new EntityCache.Entry
        {
            Id = id,
            Path = "Metadata/Terrain/Leagues/Ritual/RitualRuneInteractable",
            GridPosition = Point(x, y),
        };
        return new MechanicEntry(MechanicKind.RitualRune, entry, MechanicStatus.Available);
    }

    private static Vector2i Point(int x, int y) => new() { X = x, Y = y };
}
