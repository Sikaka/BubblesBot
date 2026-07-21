using BubblesBot.Core.Knowledge;

namespace BubblesBot.Tests;

public sealed class UltimatumModDangerTests
{
    [Theory]
    [InlineData("RevenantDaemon9", "New Stalking Ruin", "")]
    [InlineData("LeagueNewId", "Unknown modifier", "Monsters inflict Ruin")]
    [InlineData("LeagueNewId", "Restless Ground III", "")]
    public void DangerousFamiliesFailClosed(string id, string name, string description)
        => Assert.Equal(UltimatumModDanger.BlockedValue,
            UltimatumModDanger.GetDanger(id, name, description));

    [Fact]
    public void RuinCannotBeEnabledByAnOverride()
    {
        var overrides = new Dictionary<string, int> { ["RevenantDaemon2"] = -100 };

        Assert.Equal(UltimatumModDanger.BlockedValue,
            UltimatumModDanger.GetDanger("RevenantDaemon2", "Stalking Ruin II", "", overrides));
    }

    [Fact]
    public void LimitedArenaIsPreferred()
        => Assert.Equal(-1, UltimatumModDanger.GetDanger("Radius1", "Limited Arena", ""));
}
