using BubblesBot.Bot.Input;
using BubblesBot.Bot.Overlay.Native;
using BubblesBot.Core;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Visual-only recovery host used when normal IngameState bootstrap is unavailable.</summary>
internal static class CharacterSelectRecoveryHost
{
    public static int Inspect(ProcessHandle process)
    {
        var hwnd = OverlayNative.FindWindowForProcess(process.ProcessId);
        if (hwnd == 0 || !OverlayNative.GetWindowRect(hwnd, out var rect))
            return Fail("PoE window geometry is unavailable");
        var window = new WindowInfo(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        var visual = CharacterSelectVisualOracle.Read(window);
        Console.WriteLine($"Character-select inspection: {visual.Detail}");
        if (!visual.CaptureSucceeded || !visual.IsCharacterSelect
            || !visual.IdentityConfigured || !visual.NameMatches || !visual.SelectedRowMatches)
            return Fail("configured character-selection identity did not match");
        Console.WriteLine("Result: Passed — configured name, selection highlight, and Play frame matched; no input sent.");
        return 0;
    }

    public static async Task<int> RunAsync(ProcessHandle process, LiveTestOptions options)
    {
        if (options.Command != LiveTestCommand.Run || options.Phase is null)
            return Fail("explicit live-test command and phase are required");
        if (options.Phase != LiveTestPhase.Research || options.Iterations != 1)
            return Fail("pre-bootstrap Play recovery is research-only and runs once");
        if (!options.Armed || !options.SetupConfirmed)
            return Fail("Play recovery requires --arm and --confirm-setup");
        if (string.IsNullOrWhiteSpace(options.ExpectedCharacter)
            || !CharacterSelectVisualOracle.ConfigurationMatchesCharacter(options.ExpectedCharacter))
            return Fail("Play recovery requires --expect-character to match the private character-selection profile");

        var hwnd = OverlayNative.FindWindowForProcess(process.ProcessId);
        if (hwnd == 0 || !OverlayNative.GetWindowRect(hwnd, out var rect))
            return Fail("PoE window geometry is unavailable");
        var window = new WindowInfo(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        if (!OverlayNative.IsForeground(hwnd))
            return Fail("PoE is not foreground");

        var prepared = CharacterSelectVisualOracle.Read(window);
        Console.WriteLine($"Character-select recovery preflight: {prepared.Detail}");
        if (!prepared.CaptureSucceeded || !prepared.IsCharacterSelect
            || !prepared.IdentityConfigured || !prepared.NameMatches || !prepared.SelectedRowMatches)
            return Fail("configured character-selection identity did not match");

        for (var remaining = options.CountdownSeconds; remaining > 0; remaining--)
        {
            if (!OverlayNative.IsForeground(hwnd)) return Fail("foreground lost during countdown");
            Console.Write($"\rClicking verified Play in {remaining}s... ");
            await Task.Delay(1_000);
        }
        Console.WriteLine("\rClicking verified Play now.          ");

        var immediate = CharacterSelectVisualOracle.Read(window);
        if (!OverlayNative.IsForeground(hwnd)
            || !immediate.CaptureSucceeded || !immediate.IsCharacterSelect
            || !immediate.IdentityConfigured || !immediate.NameMatches || !immediate.SelectedRowMatches)
            return Fail("character-selection identity changed immediately before Play");

        var input = new InputRouter();
        var point = window.ToScreen(
            CharacterSelectVisualOracle.PlayClientX,
            CharacterSelectVisualOracle.PlayClientY);
        var ticket = input.Click(
            point.X,
            point.Y,
            ClickIntent.InteractUi,
            "pre-bootstrap Play on visually verified configured character",
            () =>
            {
                var visual = CharacterSelectVisualOracle.Read(window);
                return visual.CaptureSucceeded && !visual.IsCharacterSelect;
            },
            timeoutMs: 5_000);
        if (!ticket.Accepted || ticket.Token is null)
            return Fail("production input router suppressed Play");

        while (!ticket.IsResolved)
        {
            if (!OverlayNative.IsForeground(hwnd))
            {
                input.CancelAll();
                return Fail("foreground lost while Play was pending; input cancelled");
            }
            input.Tick();
            await Task.Delay(16);
        }
        input.Tick();
        if (ticket.Token.Outcome != ActionOutcome.Confirmed)
            return Fail($"Play outcome was {ticket.Token.Outcome}");

        Console.WriteLine("Result: Passed — verified configured-character Play left character selection.");
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"Character-select recovery blocked: {message}");
        return 3;
    }
}
