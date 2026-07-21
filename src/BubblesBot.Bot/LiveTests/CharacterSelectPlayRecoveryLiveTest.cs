namespace BubblesBot.Bot.LiveTests;

/// <summary>Catalog entry for the pre-bootstrap visual Play recovery host.</summary>
public sealed class CharacterSelectPlayRecoveryLiveTest : ILiveTestCase
{
    public const string TestId = "U-10-character-select-play-recovery";
    public string Id => TestId;
    public string Name => "Character-select Play recovery";
    public string Description => "Before normal bootstrap, visually verifies the locally configured character selection and routes one Play click through InputRouter.";
    public string ManualSetup => "Leave character selection open with the locally configured character selected, dismiss desktop dialogs, and keep PoE foreground at 1920x1080.";
    public LiveTestMutation Mutation => LiveTestMutation.Reversible;
    public bool DrivesInput => true;
    public bool RequiresInGameAtStart => false;

    public Task<LiveTestCaseResult> RunAsync(LiveTestContext context, CancellationToken cancellationToken)
        => Task.FromResult(LiveTestCaseResult.Blocked(
            "this recovery test must run through its pre-bootstrap host", "WrongHost"));
}
