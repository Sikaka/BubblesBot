namespace BubblesBot.Bot.LiveTests;

public enum LiveTestMutation
{
    ReadOnly,
    Reversible,
    Economic,
    Irreversible,
}

public enum LiveTestOutcome
{
    Passed,
    Failed,
    Blocked,
    TimedOut,
    Cancelled,
}

public sealed record LiveTestCaseResult(LiveTestOutcome Outcome, string Classification, string Summary)
{
    public static LiveTestCaseResult Pass(string summary, string classification = "CompletedExpected")
        => new(LiveTestOutcome.Passed, classification, summary);

    public static LiveTestCaseResult Fail(string summary, string classification = "AssertionFailed")
        => new(LiveTestOutcome.Failed, classification, summary);

    public static LiveTestCaseResult Blocked(string summary, string classification = "PreflightBlocked")
        => new(LiveTestOutcome.Blocked, classification, summary);
}

public interface ILiveTestCase
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    string ManualSetup { get; }
    LiveTestMutation Mutation { get; }
    bool DrivesInput { get; }
    bool RequiresInGameAtStart => true;
    bool RequiresExpectedReward => false;
    IReadOnlySet<string> AllowedBlockingPanels => new HashSet<string>(StringComparer.Ordinal);

    Task<LiveTestCaseResult> RunAsync(LiveTestContext context, CancellationToken cancellationToken);
}
