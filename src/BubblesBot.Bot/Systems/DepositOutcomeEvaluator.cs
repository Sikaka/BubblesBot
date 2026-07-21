namespace BubblesBot.Bot.Systems;

public enum DepositOutcome { Continue, Complete, Blocked }

public readonly record struct DepositAssessment(
    DepositOutcome Outcome, int Depositable, int Actionable, int Blacklisted, int MissingRect);

public static class DepositOutcomeEvaluator
{
    public static DepositAssessment Evaluate(
        int depositable, int actionable, int blacklisted, int missingRect)
    {
        if (depositable <= 0) return new(DepositOutcome.Complete, 0, 0, blacklisted, missingRect);
        if (actionable > 0) return new(DepositOutcome.Continue, depositable, actionable, blacklisted, missingRect);
        return new(DepositOutcome.Blocked, depositable, 0, blacklisted, missingRect);
    }
}
