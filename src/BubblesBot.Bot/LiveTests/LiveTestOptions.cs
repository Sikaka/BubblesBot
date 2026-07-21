using System.Globalization;

namespace BubblesBot.Bot.LiveTests;

public enum LiveTestCommand
{
    None,
    List,
    Run,
}

public enum LiveTestPhase
{
    Research,
    Single,
    Repeatable,
}

public sealed record LiveTestOptions(
    LiveTestCommand Command,
    string? TestId,
    LiveTestPhase? Phase,
    bool Armed,
    bool Commit,
    bool SetupConfirmed,
    string? ExpectedCharacter,
    uint? ExpectedAreaHash,
    int TimeoutSeconds,
    int CountdownSeconds,
    int Iterations,
    string ArtifactRoot,
    string? ExpectedReward)
{
    public const int DefaultTimeoutSeconds = 600;
    public const int MaximumTimeoutSeconds = 720;
    public const int DefaultCountdownSeconds = 5;

    public static LiveTestParseResult Parse(string[] args)
    {
        var wantsList = args.Contains("--list-live-tests", StringComparer.Ordinal);
        var testIndex = Array.IndexOf(args, "--live-test");
        if (!wantsList && testIndex < 0)
            return LiveTestParseResult.SuccessResult(new LiveTestOptions(
                LiveTestCommand.None, null, null, false, false, false, null, null,
                DefaultTimeoutSeconds, DefaultCountdownSeconds, 1, DefaultArtifactRoot(), null));

        if (wantsList && testIndex >= 0)
            return LiveTestParseResult.ErrorResult("--list-live-tests and --live-test cannot be combined");

        if (wantsList)
        {
            var unexpected = args.Where(x => x != "--list-live-tests").ToArray();
            return unexpected.Length == 0
                ? LiveTestParseResult.SuccessResult(new LiveTestOptions(
                    LiveTestCommand.List, null, null, false, false, false, null, null,
                    DefaultTimeoutSeconds, DefaultCountdownSeconds, 1, DefaultArtifactRoot(), null))
                : LiveTestParseResult.ErrorResult($"unexpected argument(s) with --list-live-tests: {string.Join(" ", unexpected)}");
        }

        if (testIndex + 1 >= args.Length || args[testIndex + 1].StartsWith("--", StringComparison.Ordinal))
            return LiveTestParseResult.ErrorResult("--live-test requires a test ID");

        var testId = args[testIndex + 1];
        LiveTestPhase? phase = null;
        var armed = false;
        var commit = false;
        var setupConfirmed = false;
        string? expectedCharacter = null;
        uint? expectedAreaHash = null;
        var timeout = DefaultTimeoutSeconds;
        var countdown = DefaultCountdownSeconds;
        var iterations = 1;
        var artifacts = DefaultArtifactRoot();
        string? expectedReward = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--live-test":
                    i++;
                    break;
                case "--phase":
                    if (!TryTake(args, ref i, out var phaseText))
                        return LiveTestParseResult.ErrorResult("--phase requires research, single, or repeatable");
                    phase = phaseText.ToLowerInvariant() switch
                    {
                        "research" => LiveTestPhase.Research,
                        "single" => LiveTestPhase.Single,
                        "repeatable" or "repeat" => LiveTestPhase.Repeatable,
                        _ => null,
                    };
                    if (phase is null)
                        return LiveTestParseResult.ErrorResult($"unknown live-test phase '{phaseText}'");
                    break;
                case "--arm":
                    armed = true;
                    break;
                case "--commit":
                    commit = true;
                    break;
                case "--confirm-setup":
                    setupConfirmed = true;
                    break;
                case "--expect-character":
                    if (!TryTake(args, ref i, out expectedCharacter) || string.IsNullOrWhiteSpace(expectedCharacter))
                        return LiveTestParseResult.ErrorResult("--expect-character requires a non-empty character name");
                    break;
                case "--expect-area-hash":
                    if (!TryTake(args, ref i, out var hashText) || !TryParseUInt(hashText, out var hash))
                        return LiveTestParseResult.ErrorResult("--expect-area-hash requires a decimal or 0x-prefixed uint");
                    expectedAreaHash = hash;
                    break;
                case "--expect-reward":
                    if (!TryTake(args, ref i, out expectedReward) || string.IsNullOrWhiteSpace(expectedReward))
                        return LiveTestParseResult.ErrorResult("--expect-reward requires a non-empty exact base name");
                    break;
                case "--timeout-seconds":
                    if (!TryTakeInt(args, ref i, 1, MaximumTimeoutSeconds, out timeout))
                        return LiveTestParseResult.ErrorResult($"--timeout-seconds must be 1..{MaximumTimeoutSeconds}");
                    break;
                case "--countdown-seconds":
                    if (!TryTakeInt(args, ref i, 0, 30, out countdown))
                        return LiveTestParseResult.ErrorResult("--countdown-seconds must be 0..30");
                    break;
                case "--iterations":
                    if (!TryTakeInt(args, ref i, 1, 20, out iterations))
                        return LiveTestParseResult.ErrorResult("--iterations must be 1..20");
                    break;
                case "--artifacts":
                    if (!TryTake(args, ref i, out artifacts) || string.IsNullOrWhiteSpace(artifacts))
                        return LiveTestParseResult.ErrorResult("--artifacts requires a directory path");
                    artifacts = Path.GetFullPath(artifacts);
                    break;
                case "--hp" or "--mana":
                    // Bootstrap fallback arguments remain valid in live-test mode.
                    if (!TryTake(args, ref i, out _))
                        return LiveTestParseResult.ErrorResult($"{arg} requires an integer value");
                    break;
                default:
                    return LiveTestParseResult.ErrorResult($"unknown live-test argument '{arg}'");
            }
        }

        if (phase is null)
            return LiveTestParseResult.ErrorResult("--phase is required for --live-test");

        return LiveTestParseResult.SuccessResult(new LiveTestOptions(
            LiveTestCommand.Run, testId, phase, armed, commit, setupConfirmed,
            expectedCharacter?.Trim(), expectedAreaHash, timeout, countdown, iterations, artifacts,
            expectedReward?.Trim()));
    }

    private static string DefaultArtifactRoot()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BubblesBot", "live-tests");

    private static bool TryTake(string[] args, ref int index, out string value)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = string.Empty;
            return false;
        }
        value = args[++index];
        return true;
    }

    private static bool TryTakeInt(string[] args, ref int index, int min, int max, out int value)
    {
        value = 0;
        return TryTake(args, ref index, out var text)
            && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
            && value >= min && value <= max;
    }

    private static bool TryParseUInt(string text, out uint value)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        return uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}

public sealed record LiveTestParseResult(LiveTestOptions? Options, string? Error)
{
    public bool Success => Options is not null && Error is null;
    public static LiveTestParseResult SuccessResult(LiveTestOptions options) => new(options, null);
    public static LiveTestParseResult ErrorResult(string error) => new(null, error);
}
