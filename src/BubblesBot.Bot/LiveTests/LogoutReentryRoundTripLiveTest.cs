using BubblesBot.Bot.Input;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Full persistent logout-to-character-selection and Play re-entry round trip.</summary>
public sealed class LogoutReentryRoundTripLiveTest : ILiveTestCase
{
    private const int EscapeScanCode = 0x01;
    private const int ExitCharacterSelectClientX = 960;
    private const int ExitCharacterSelectClientY = 472;

    public string Id => "U-10-logout-reentry-roundtrip";
    public string Name => "Logout and re-entry round trip";
    public string Description => "Opens the classified menu, logs out through its fifth row, validates the locally configured selected-character frame, clicks Play, waits through the no-input transition, and proves the same character and safe zone returned.";
    public string ManualSetup => "On the locally configured character in a safe town or hideout, close all UI and the system menu, hold no item, dismiss desktop dialogs, and leave PoE foreground at 1920x1080. The same character must already be selected on the character list.";
    public LiveTestMutation Mutation => LiveTestMutation.Reversible;
    public bool DrivesInput => true;

    public async Task<LiveTestCaseResult> RunAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        var baseline = context.Snapshot();
        var character = baseline.Player?.CharacterName ?? string.Empty;
        var areaHash = baseline.AreaHash;
        var window = baseline.Window;
        var configuredCharacter = CharacterSelectVisualOracle.ConfigurationMatchesCharacter(character);
        context.Check(configuredCharacter, "round-trip private character baseline", character.Length > 0 ? "matched" : "missing");
        context.Check(areaHash != 0, "round-trip safe-zone baseline", $"0x{areaHash:X8}");
        context.Check(window.Width == SystemMenuVisualOracle.SupportedWidth
            && window.Height == SystemMenuVisualOracle.SupportedHeight,
            "round-trip visual geometry", $"{window.Width}x{window.Height} at ({window.OriginX},{window.OriginY})");
        if (!configuredCharacter
            || areaHash == 0
            || window.Width != SystemMenuVisualOracle.SupportedWidth
            || window.Height != SystemMenuVisualOracle.SupportedHeight)
            return LiveTestCaseResult.Blocked("round-trip character/area/geometry baseline is unavailable", "RoundTripBaselineMissing");

        var clean = SystemMenuVisualOracle.Read(window);
        context.Check(clean.CaptureSucceeded && !clean.IsOpen, "clean HUD before round trip", clean.Detail);
        if (!clean.CaptureSucceeded || clean.IsOpen)
            return LiveTestCaseResult.Blocked("system menu must be closed at the round-trip baseline", "CleanHudSetupMissing");

        var open = await context.VerifiedTapScanCodeAsync(
            EscapeScanCode,
            ClickIntent.InteractUi,
            "open logout menu through Escape scan code 0x01",
            () => SystemMenuVisualOracle.Read(context.Window).IsOpen,
            timeoutMs: 2_000,
            cancellationToken);
        if (open != ActionOutcome.Confirmed)
            return LiveTestCaseResult.Fail("Escape scan code did not open the classified logout menu", "MenuOpenFailed");
        if (!await context.WaitForInputIdleAsync("after round-trip menu open", 1_500, cancellationToken))
            return LiveTestCaseResult.Fail("input did not settle after menu open", "InputSettleFailed");
        var menu = SystemMenuVisualOracle.Read(context.Window);
        context.Check(menu.CaptureSucceeded && menu.IsOpen, "logout menu immediate revalidation", menu.Detail);
        if (!menu.CaptureSucceeded || !menu.IsOpen)
            return LiveTestCaseResult.Fail("logout menu was not present immediately before fifth-row click", "MenuRevalidationFailed");

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
            "select classified fifth-row Exit to Character Selection",
            () => context.GameState is not (GameStateKind.InGame or GameStateKind.GateDisabled),
            timeoutMs: 5_000,
            cancellationToken);
        if (exit != ActionOutcome.Confirmed)
            return LiveTestCaseResult.Fail("fifth-row logout target did not make the world leave InGame", "LogoutDispatchFailed");

        var characterScreen = await context.WaitUntilAsync(
            "configured character-selection frame",
            () =>
            {
                var visual = CharacterSelectVisualOracle.Read(context.Window);
                return visual.CaptureSucceeded && visual.IsCharacterSelect
                    && visual.IdentityConfigured && visual.NameMatches && visual.SelectedRowMatches;
            },
            timeoutMs: 25_000,
            cancellationToken,
            pollMs: 100);
        if (!characterScreen)
            return LiveTestCaseResult.Fail("configured character-selection frame did not appear", "CharacterSelectionVisualMissing");

        var selection = CharacterSelectVisualOracle.Read(context.Window);
        context.Check(selection.CaptureSucceeded && selection.IsCharacterSelect,
            "character-selection Play/frame identity", selection.Detail);
        context.Check(selection.IdentityConfigured,
            "private character identity configured before Play", selection.Detail);
        context.Check(selection.NameMatches,
            "configured name identity before Play", selection.Detail);
        context.Check(selection.SelectedRowMatches,
            "configured selected identity before Play", selection.Detail);
        if (!selection.CaptureSucceeded || !selection.IsCharacterSelect
            || !selection.IdentityConfigured || !selection.NameMatches || !selection.SelectedRowMatches)
            return LiveTestCaseResult.Fail("character-selection identity changed before Play dispatch", "PlayPreconditionChanged");

        if (!await context.WaitForInputIdleAsync("before character-selection Play", 1_500, cancellationToken))
            return LiveTestCaseResult.Fail("input did not settle before Play", "InputSettleFailed");
        var playPoint = context.Window.ToScreen(
            CharacterSelectVisualOracle.PlayClientX,
            CharacterSelectVisualOracle.PlayClientY);
        var play = await context.VerifiedClickAsync(
            playPoint.X,
            playPoint.Y,
            ClickIntent.InteractUi,
            "click Play on visually verified selected character",
            () =>
            {
                var visual = CharacterSelectVisualOracle.Read(context.Window);
                return visual.CaptureSucceeded && !visual.IsCharacterSelect;
            },
            timeoutMs: 5_000,
            cancellationToken);
        if (play != ActionOutcome.Confirmed)
            return LiveTestCaseResult.Fail("Play did not leave the verified character-selection frame", "PlayDispatchFailed");

        var loaded = await context.WaitUntilAsync(
            "same character loaded after Play",
            () => context.GameState == GameStateKind.InGame,
            timeoutMs: 45_000,
            cancellationToken,
            pollMs: 100);
        if (!loaded)
            return LiveTestCaseResult.Fail($"Play did not rebuild an InGame world; state={context.GameState}", "ReentryLoadFailed");

        context.SetAllowedInputStates(GameStateKind.InGame);
        context.SetExpectedArea(areaHash);
        var restored = context.Snapshot();
        var restoredCharacter = restored.Player?.CharacterName ?? string.Empty;
        context.Check(string.Equals(restoredCharacter, character, StringComparison.Ordinal),
            "same character restored", string.Equals(restoredCharacter, character, StringComparison.Ordinal) ? "matched" : "mismatch");
        context.Check(restored.AreaHash == areaHash,
            "same safe zone restored", $"expected=0x{areaHash:X8} observed=0x{restored.AreaHash:X8}");
        var life = restored.Player?.Life ?? default;
        context.Check(restored.Player is not null && life.Max > 0 && life.Current > 0 && life.Current <= life.Max,
            "restored character alive", $"hp={life.Current}/{life.Max}");
        var cleanHudReached = await context.WaitUntilAsync(
            "clean HUD frame after re-entry fade",
            () =>
            {
                var visual = SystemMenuVisualOracle.Read(context.Window);
                return visual.CaptureSucceeded && !visual.IsOpen;
            },
            timeoutMs: 10_000,
            cancellationToken,
            pollMs: 100);
        var finalVisual = SystemMenuVisualOracle.Read(context.Window);
        context.Check(cleanHudReached && finalVisual.CaptureSucceeded && !finalVisual.IsOpen,
            "panel-free HUD restored after re-entry", finalVisual.Detail);
        if (!string.Equals(restoredCharacter, character, StringComparison.Ordinal)
            || restored.AreaHash != areaHash
            || restored.Player is null || life.Max <= 0 || life.Current <= 0 || life.Current > life.Max
            || !cleanHudReached || !finalVisual.CaptureSucceeded || finalVisual.IsOpen)
            return LiveTestCaseResult.Fail("re-entry loaded a different or invalid character/area state", "ReentryIdentityMismatch");

        // Give the production edge-action window room before a repeatable iteration starts.
        await Task.Delay(500, cancellationToken);
        return LiveTestCaseResult.Pass(
            $"Configured character logged out and re-entered safe zone 0x{areaHash:X8} through verified menu, selection, and Play states",
            "CompletedAndRestored");
    }
}
