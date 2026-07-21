using BubblesBot.Bot.Modes;
namespace BubblesBot.Tests;

public sealed class DeliriumPackPolicyTests
{
    [Fact]
    public void DwellExpiresAtConfiguredBudget()
    {
        var entered = TimeSpan.FromSeconds(10);
        Assert.False(DeliriumPackPolicy.DwellExpired(entered, TimeSpan.FromSeconds(13.49), 3.5));
        Assert.True(DeliriumPackPolicy.DwellExpired(entered, TimeSpan.FromSeconds(13.5), 3.5));
    }
}
