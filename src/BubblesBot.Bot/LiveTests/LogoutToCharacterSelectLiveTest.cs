using BubblesBot.Bot.Input;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Stage one of logout/re-entry: reach character selection and stop.</summary>
public sealed class LogoutToCharacterSelectLiveTest : ILiveTestCase
{
    private const int EscapeScanCode = 0x01;
    private const int ExitCharacterSelectClientX = 960;
    private const int ExitCharacterSelectClientY = 472;

    public string Id => "U-10-logout-to-character-select";
    public string Name => "Logout to character selection";
    public string Description => "From a clean safe-zone HUD, opens the visually classified system menu, selects its fifth row, proves the world state exits InGame, waits for character selection, and stops without clicking Play.";
    public string ManualSetup => "On the locally configured character in a safe town or hideout, close ordinary UI and the system menu, hold no item, dismiss desktop dialogs, and leave PoE foreground at 1920x1080. The test ends at character selection.";
    public LiveTestMutation Mutation => LiveTestMutation.Reversible;
    public bool DrivesInput => true;

    public async Task<LiveTestCaseResult> RunAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        var baseline = context.Snapshot();
        var character = baseline.Player?.CharacterName ?? string.Empty;
        var areaHash = baseline.AreaHash;
        var window = baseline.Window;
        var configuredCharacter = CharacterSelectVisualOracle.ConfigurationMatchesCharacter(character);
        context.Check(configuredCharacter, "logout private character baseline", character.Length > 0 ? "matched" : "missing");
        context.Check(areaHash != 0, "logout area baseline", $"0x{areaHash:X8}");
        context.Check(window.Width == SystemMenuVisualOracle.SupportedWidth
            && window.Height == SystemMenuVisualOracle.SupportedHeight,
            "supported logout visual geometry", $"{window.Width}x{window.Height} at ({window.OriginX},{window.OriginY})");
        if (!configuredCharacter || areaHash == 0
            || window.Width != SystemMenuVisualOracle.SupportedWidth
            || window.Height != SystemMenuVisualOracle.SupportedHeight)
            return LiveTestCaseResult.Blocked("logout baseline identity/geometry is unavailable", "LogoutBaselineMissing");

        var clean = SystemMenuVisualOracle.Read(window);
        context.Check(clean.CaptureSucceeded && !clean.IsOpen, "clean HUD before logout", clean.Detail);
        if (!clean.CaptureSucceeded || clean.IsOpen)
            return LiveTestCaseResult.Blocked("system menu must be closed at the logout baseline", "CleanHudSetupMissing");

        var open = await context.VerifiedTapScanCodeAsync(
            EscapeScanCode,
            ClickIntent.InteractUi,
            "open system menu for logout through Escape scan code 0x01",
            () => SystemMenuVisualOracle.Read(context.Snapshot().Window).IsOpen,
            timeoutMs: 2_000,
            cancellationToken);
        if (open != ActionOutcome.Confirmed)
            return LiveTestCaseResult.Fail("Escape scan code did not open the classified logout menu", "MenuOpenFailed");
        if (!await context.WaitForInputIdleAsync("after logout menu open", 1_500, cancellationToken))
            return LiveTestCaseResult.Fail("input did not settle after logout menu open", "InputSettleFailed");

        var menu = SystemMenuVisualOracle.Read(context.Snapshot().Window);
        context.Check(menu.CaptureSucceeded && menu.IsOpen, "logout menu visual revalidation", menu.Detail);
        if (!menu.CaptureSucceeded || !menu.IsOpen)
            return LiveTestCaseResult.Fail("six-button menu was not present immediately before logout click", "MenuRevalidationFailed");

        // The click starts in InGame but its verified outcome necessarily crosses transition,
        // loading, and the currently unclassified character-selection state.
        context.SetAllowedInputStates(
            GameStateKind.InGame,
            GameStateKind.Transition,
            GameStateKind.Loading,
            GameStateKind.Other);
        context.SetExpectedArea(null);
        var exitPoint = window.ToScreen(ExitCharacterSelectClientX, ExitCharacterSelectClientY);
        var exit = await context.VerifiedClickAsync(
            exitPoint.X,
            exitPoint.Y,
            ClickIntent.InteractUi,
            "select fifth-row Exit to Character Selection visual target",
            () => context.GameState is GameStateKind.Transition or GameStateKind.Loading or GameStateKind.Other,
            timeoutMs: 5_000,
            cancellationToken);
        if (exit != ActionOutcome.Confirmed)
            return LiveTestCaseResult.Fail("character-selection target did not make the world leave InGame", "LogoutDispatchFailed");

        var reached = await context.WaitUntilAsync(
            "prepared character-selection visual frame",
            () =>
            {
                var visual = CharacterSelectVisualOracle.Read(context.Window);
                return visual.CaptureSucceeded && visual.IsCharacterSelect
                    && visual.IdentityConfigured && visual.NameMatches && visual.SelectedRowMatches;
            },
            timeoutMs: 20_000,
            cancellationToken,
            pollMs: 100);
        if (!reached)
            return LiveTestCaseResult.Fail($"logout left InGame but the prepared character-selection frame did not appear; state={context.GameState}", "CharacterSelectionStateMissing");
        var selection = CharacterSelectVisualOracle.Read(context.Window);
        context.Check(context.GameState == GameStateKind.Transition,
            "character-selection top-level observation", $"state={context.GameState}");
        context.Check(selection.CaptureSucceeded && selection.IsCharacterSelect
            && selection.IdentityConfigured && selection.NameMatches && selection.SelectedRowMatches,
            "declared character-selection terminal frame", selection.Detail);

        return LiveTestCaseResult.Pass(
            $"Configured character left area 0x{areaHash:X8} through the classified fifth-row target and reached the verified character-selection frame in Transition; Play was not clicked",
            "DeclaredCharacterSelectionTerminal");
    }
}
