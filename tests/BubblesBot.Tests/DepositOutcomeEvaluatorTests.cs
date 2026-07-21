using BubblesBot.Bot.Systems;

namespace BubblesBot.Tests;

public sealed class DepositOutcomeEvaluatorTests
{
    [Fact]
    public void RemainingBlacklistedItemsCannotReportSuccess()
    {
        var result = DepositOutcomeEvaluator.Evaluate(3, 0, 3, 0);
        Assert.Equal(DepositOutcome.Blocked, result.Outcome);
        Assert.Equal(3, result.Depositable);
    }

    [Fact]
    public void RemainingUnreadableItemsCannotReportSuccess()
        => Assert.Equal(DepositOutcome.Blocked,
            DepositOutcomeEvaluator.Evaluate(2, 0, 0, 2).Outcome);

    [Fact]
    public void EmptyInventoryIsTheOnlyCompleteOutcome()
    {
        Assert.Equal(DepositOutcome.Complete,
            DepositOutcomeEvaluator.Evaluate(0, 0, 0, 0).Outcome);
        Assert.Equal(DepositOutcome.Continue,
            DepositOutcomeEvaluator.Evaluate(2, 1, 1, 0).Outcome);
    }
}
