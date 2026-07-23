using System.Diagnostics;
using BubblesBot.Bot.Input;
using BubblesBot.Bot.Overlay.Native;
using BubblesBot.Core;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

public static class LiveTestHost
{
    public static async Task<int> RunAsync(
        ProcessHandle process,
        MemoryReader reader,
        nint ingameDataAddress,
        nint ingameStateAddress,
        IReadOnlyList<nint> theGameSlots,
        LiveTestOptions options)
    {
        var test = LiveTestRegistry.Find(options.TestId ?? string.Empty);
        if (test is null)
        {
            Console.Error.WriteLine($"Unknown live-test ID '{options.TestId}'. Use --list-live-tests.");
            return 64;
        }

        var validationError = ValidateInvocation(test, options);
        if (validationError is not null)
        {
            Console.Error.WriteLine($"Live-test invocation blocked: {validationError}");
            Console.Error.WriteLine($"Required setup: {test.ManualSetup}");
            return 64;
        }

        var hwnd = OverlayNative.FindWindowForProcess(process.ProcessId);
        var gameState = new GameStateView(reader, theGameSlots);
        var input = new InputRouter();
        using var recorder = new LiveTestRecorder(
            options, test, process, ingameDataAddress, ingameStateAddress);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
        using var userCancel = new CancellationTokenSource();
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, userCancel.Token);

        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            userCancel.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        var context = new LiveTestContext(
            reader, ingameDataAddress, ingameStateAddress, hwnd, gameState,
            options, recorder, input, combined.Token);
        context.SetAllowedBlockingPanels(test.AllowedBlockingPanels);
        var started = Stopwatch.GetTimestamp();
        LiveTestCaseResult result;
        string? exceptionText = null;

        try
        {
            Console.WriteLine();
            Console.WriteLine($"Live test: {test.Id} — {test.Name}");
            Console.WriteLine($"Phase: {options.Phase}  Mutation: {test.Mutation}  Timeout: {options.TimeoutSeconds}s");
            Console.WriteLine($"Evidence: {recorder.EvidenceDirectory}");
            Console.WriteLine($"Setup: {test.ManualSetup}");

            // Input tests require PoE foreground. Pull it forward ourselves so a headless run
            // doesn't hinge on a human clicking the game first; the foreground check still gates.
            if (test.DrivesInput && hwnd != 0 && !OverlayNative.IsForeground(hwnd))
            {
                OverlayNative.ForceForeground(hwnd);
                await Task.Delay(400, combined.Token);
            }

            if (!RunPreflight(context, test, options, hwnd, gameState))
            {
                result = LiveTestCaseResult.Blocked("generic preflight failed");
            }
            else
            {
                if (test.DrivesInput && options.CountdownSeconds > 0)
                {
                    for (var remaining = options.CountdownSeconds; remaining > 0; remaining--)
                    {
                        combined.Token.ThrowIfCancellationRequested();
                        Console.Write($"\rStarting input-capable test in {remaining}s... ");
                        await Task.Delay(1000, combined.Token);
                    }
                    Console.WriteLine("\rStarting input-capable test now.          ");
                }

                result = LiveTestCaseResult.Fail("test did not run", "HarnessError");
                for (var iteration = 1; iteration <= options.Iterations; iteration++)
                {
                    combined.Token.ThrowIfCancellationRequested();
                    context.Observe("iteration", $"{iteration}/{options.Iterations} starting",
                        new Dictionary<string, object?>
                        {
                            ["iteration"] = iteration,
                            ["total"] = options.Iterations,
                        });

                    var passedBefore = context.PassedChecks;
                    result = await test.RunAsync(context, combined.Token);
                    if (result.Outcome == LiveTestOutcome.Passed && context.FailedChecks > 0)
                        result = LiveTestCaseResult.Fail(
                            $"test returned PASS but {context.FailedChecks} assertion(s) failed",
                            "AssertionFailed");
                    else if (result.Outcome == LiveTestOutcome.Passed && context.PassedChecks == passedBefore)
                        result = LiveTestCaseResult.Fail("iteration made no passing assertions", "NoAssertions");

                    context.Observe("iteration", $"{iteration}/{options.Iterations} {result.Outcome}",
                        new Dictionary<string, object?>
                        {
                            ["iteration"] = iteration,
                            ["outcome"] = result.Outcome.ToString(),
                            ["classification"] = result.Classification,
                        });
                    if (result.Outcome != LiveTestOutcome.Passed)
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            result = new LiveTestCaseResult(LiveTestOutcome.TimedOut, "HardTimeout",
                $"hard timeout reached after {options.TimeoutSeconds}s");
        }
        catch (OperationCanceledException)
        {
            result = new LiveTestCaseResult(LiveTestOutcome.Cancelled, "OperatorCancelled",
                "operator cancelled the live test");
        }
        catch (Exception ex)
        {
            exceptionText = ex.ToString();
            result = LiveTestCaseResult.Fail($"{ex.GetType().Name}: {ex.Message}", "UnhandledException");
        }
        finally
        {
            input.CancelAll();
            Console.CancelKeyPress -= cancelHandler;
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        recorder.Complete(result, context.PassedChecks, context.FailedChecks, elapsed, exceptionText);

        Console.WriteLine();
        Console.WriteLine($"Result: {result.Outcome} — {result.Classification} — {result.Summary}");
        Console.WriteLine($"Checks: {context.PassedChecks} passed, {context.FailedChecks} failed");
        Console.WriteLine($"Evidence: {recorder.EvidenceDirectory}");
        return result.Outcome == LiveTestOutcome.Passed ? 0 : 3;
    }

    public static string? ValidateInvocation(ILiveTestCase test, LiveTestOptions options)
    {
        if (options.Command != LiveTestCommand.Run || options.Phase is null)
            return "a Run command and explicit phase are required";

        if (options.Phase == LiveTestPhase.Repeatable && options.Iterations < 3)
            return "repeatable phase requires --iterations of at least 3";
        if (options.Phase != LiveTestPhase.Repeatable && options.Iterations != 1)
            return "research and single phases require exactly one iteration";

        if (test.DrivesInput)
        {
            if (!options.Armed) return "input-driving tests require --arm";
            if (!options.SetupConfirmed) return "input-driving tests require --confirm-setup";
            if (string.IsNullOrWhiteSpace(options.ExpectedCharacter))
                return "input-driving tests require --expect-character";
            if (options.ExpectedAreaHash is null)
                return "input-driving tests require --expect-area-hash";
        }

        if (test.Mutation is LiveTestMutation.Economic or LiveTestMutation.Irreversible
            && !options.Commit)
            return $"{test.Mutation} tests require --commit in every phase";
        if (test.RequiresExpectedReward && string.IsNullOrWhiteSpace(options.ExpectedReward))
            return "this test requires --expect-reward with an exact live reward base name";

        return null;
    }

    private static bool RunPreflight(
        LiveTestContext context,
        ILiveTestCase test,
        LiveTestOptions options,
        nint hwnd,
        GameStateView gameState)
    {
        var ok = true;
        ok &= context.Check(hwnd != 0, "PoE window", hwnd != 0 ? $"hwnd=0x{(long)hwnd:X}" : "not found");
        ok &= context.Check(hwnd != 0 && OverlayNative.GetWindowRect(hwnd, out var rect)
            && rect.Right > rect.Left && rect.Bottom > rect.Top,
            "window geometry", hwnd != 0 ? "readable and non-empty" : "window unavailable");

        var kind = gameState.ReadKind();
        ok &= context.Check(kind != GameStateKind.GateDisabled, "TheGame gate", $"state={kind}");
        if (test.RequiresInGameAtStart)
            ok &= context.Check(kind == GameStateKind.InGame, "initial game state", $"state={kind}");

        if (kind == GameStateKind.InGame)
        {
            var snapshot = context.Snapshot();
            var player = snapshot.Player;
            var character = player?.CharacterName ?? string.Empty;
            ok &= context.Check(player is not null, "player read", player is null ? "missing" : $"0x{(long)player.Address:X}");
            ok &= context.Check(!string.IsNullOrWhiteSpace(character), "character identity",
                string.IsNullOrWhiteSpace(character) ? "missing" : "present");
            ok &= context.Check(snapshot.AreaHash != 0, "area hash", $"0x{snapshot.AreaHash:X8}");

            if (!string.IsNullOrEmpty(options.ExpectedCharacter))
                ok &= context.Check(string.Equals(character, options.ExpectedCharacter, StringComparison.Ordinal),
                    "expected character", string.Equals(character, options.ExpectedCharacter, StringComparison.Ordinal)
                        ? "matched"
                        : "mismatch");
            if (options.ExpectedAreaHash is { } expectedHash)
                ok &= context.Check(snapshot.AreaHash == expectedHash,
                    "expected area", $"expected=0x{expectedHash:X8} observed=0x{snapshot.AreaHash:X8}");

            var life = player?.Life ?? default;
            ok &= context.Check(player is not null && life.Max > 0 && life.Current > 0 && life.Current <= life.Max,
                "player alive", $"hp={life.Current}/{life.Max}");

            var blocking = snapshot.OpenPanels.BlockingOpen()
                .Where(x => !test.AllowedBlockingPanels.Contains(x)).ToArray();
            ok &= context.Check(blocking.Length == 0, "blocking panels",
                blocking.Length == 0 ? "none" : string.Join(", ", blocking));
        }

        if (test.DrivesInput)
        {
            ok &= context.Check(options.SetupConfirmed, "manual setup confirmation", "--confirm-setup supplied");
            ok &= context.Check(hwnd != 0 && OverlayNative.IsForeground(hwnd), "foreground", "PoE must remain focused");
            var (allowed, reason) = context.CanDispatchInput();
            ok &= context.Check(allowed, "input dispatch gate", reason);
        }

        context.Observe("preflight", ok ? "PASS" : "BLOCKED");
        return ok;
    }
}
