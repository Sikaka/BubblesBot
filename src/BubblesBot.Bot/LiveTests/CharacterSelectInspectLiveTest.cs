using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Read-only prepared character-selection frame validation.</summary>
public sealed class CharacterSelectInspectLiveTest : ILiveTestCase
{
    public const string TestId = "U-10-character-select-inspect";
    public string Id => TestId;
    public string Name => "Character-selection visual inspection";
    public string Description => "Validates the 1920x1080 character-select frame, locally configured name mask, selected-row highlight, and Play control without input.";
    public string ManualSetup => "Leave the locally configured character selected on the character-selection screen and keep PoE foreground at 1920x1080. Do not click Play.";
    public LiveTestMutation Mutation => LiveTestMutation.ReadOnly;
    public bool DrivesInput => false;
    public bool RequiresInGameAtStart => false;

    public Task<LiveTestCaseResult> RunAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        var result = CharacterSelectVisualOracle.Read(context.Window);
        context.Check(context.GameState == GameStateKind.Transition,
            "character-select top-level observation", $"state={context.GameState}");
        context.Check(result.CaptureSucceeded, "character-select frame capture", result.Detail);
        context.Check(result.IsCharacterSelect, "character-select Play/frame identity", result.Detail);
        context.Check(result.IdentityConfigured, "private character identity configured", result.Detail);
        context.Check(result.NameMatches, "configured visual name identity", result.Detail);
        context.Check(result.SelectedRowMatches, "configured selected-row identity", result.Detail);
        if (!result.CaptureSucceeded || !result.IsCharacterSelect
            || !result.IdentityConfigured || !result.NameMatches || !result.SelectedRowMatches)
            return Task.FromResult(LiveTestCaseResult.Fail("prepared character-selection visual identity did not match", "CharacterSelectionVisualMismatch"));
        return Task.FromResult(LiveTestCaseResult.Pass(
            $"Configured character and selected-row state match; Play is present and untouched ({result.Detail})",
            "ReadOnlyCharacterSelectionCapture"));
    }
}
