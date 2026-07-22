using BubblesBot.Bot.Settings;
using BubblesBot.Bot.Web;

namespace BubblesBot.Tests;

public sealed class LatencyPolicyTests
{
    [Fact]
    public void ZeroAllowance_PreservesBaselineTimingsAndRetries()
    {
        var settings = new BotSettings { ActionLatencyAllowanceMs = 0 };

        Assert.Equal(1500, LatencyPolicy.TimeoutMs(1500, settings));
        Assert.Equal(20, LatencyPolicy.TimeoutSeconds(20, settings));
        Assert.Equal(4, LatencyPolicy.RetryLimit(4, settings));
    }

    [Fact]
    public void Allowance_ExtendsDeadlineAndAddsBoundedRetries()
    {
        var settings = new BotSettings { ActionLatencyAllowanceMs = 3500 };

        Assert.Equal(2500, LatencyPolicy.TimeoutMs(1500, settings));
        Assert.Equal(23.5, LatencyPolicy.TimeoutSeconds(20, settings));
        Assert.Equal(8, LatencyPolicy.RetryLimit(4, settings));
        Assert.Equal(6, LatencyPolicy.RetryLimit(2, settings, maxExtraAttempts: 4));
    }

    [Fact]
    public void Allowance_IsClampedDefensively()
    {
        Assert.Equal(2500, LatencyPolicy.TimeoutMs(1500, configuredAllowanceMs: 99_000));
    }

    [Fact]
    public void SettingRangeRejectsUnsupportedAllowance()
    {
        var settings = new BotSettings { ActionLatencyAllowanceMs = 10_001 };

        Assert.Contains(SettingsValidator.Validate(settings), error =>
            error.Path == "actionLatencyAllowanceMs");
    }
}
