using BubblesBot.Bot.LiveTests;

namespace BubblesBot.Tests;

public sealed class LiveTestOptionsTests
{
    [Fact]
    public void NormalBotArgumentsDoNotEnterLiveTestMode()
    {
        var parsed = LiveTestOptions.Parse(["--hp", "123"]);

        Assert.True(parsed.Success);
        Assert.Equal(LiveTestCommand.None, parsed.Options!.Command);
    }

    [Fact]
    public void LiveTestRequiresExplicitPhase()
    {
        var parsed = LiveTestOptions.Parse(["--live-test", "setup-inspect"]);

        Assert.False(parsed.Success);
        Assert.Contains("--phase is required", parsed.Error);
    }

    [Fact]
    public void ParsesGuardedInputInvocationAndHexAreaHash()
    {
        var parsed = LiveTestOptions.Parse([
            "--live-test", "H-01-input-roundtrip",
            "--phase", "single",
            "--arm",
            "--confirm-setup",
            "--expect-character", "ExampleCharacter",
            "--expect-area-hash", "0xA1B2C3D4",
            "--expect-reward", "Molten Strike",
            "--timeout-seconds", "90",
            "--countdown-seconds", "3",
        ]);

        Assert.True(parsed.Success, parsed.Error);
        var options = parsed.Options!;
        Assert.Equal(LiveTestCommand.Run, options.Command);
        Assert.Equal(LiveTestPhase.Single, options.Phase);
        Assert.True(options.Armed);
        Assert.True(options.SetupConfirmed);
        Assert.Equal("ExampleCharacter", options.ExpectedCharacter);
        Assert.Equal(0xA1B2C3D4u, options.ExpectedAreaHash);
        Assert.Equal("Molten Strike", options.ExpectedReward);
        Assert.Equal(90, options.TimeoutSeconds);
        Assert.Equal(3, options.CountdownSeconds);
        Assert.Equal(1, options.Iterations);
    }

    [Fact]
    public void RejectsTimeoutBeyondAfkWindowLimit()
    {
        var parsed = LiveTestOptions.Parse([
            "--live-test", "setup-inspect",
            "--phase", "research",
            "--timeout-seconds", "721",
        ]);

        Assert.False(parsed.Success);
        Assert.Contains("1..720", parsed.Error);
    }

    [Fact]
    public void InputTestRequiresEveryIdentityAndConsentGate()
    {
        var test = new FakeTest(LiveTestMutation.Reversible, drivesInput: true);
        var options = Options(LiveTestPhase.Single);

        Assert.Equal("input-driving tests require --arm", LiveTestHost.ValidateInvocation(test, options));
        options = options with { Armed = true };
        Assert.Equal("input-driving tests require --confirm-setup", LiveTestHost.ValidateInvocation(test, options));
        options = options with { SetupConfirmed = true };
        Assert.Equal("input-driving tests require --expect-character", LiveTestHost.ValidateInvocation(test, options));
        options = options with { ExpectedCharacter = "ExampleCharacter" };
        Assert.Equal("input-driving tests require --expect-area-hash", LiveTestHost.ValidateInvocation(test, options));
        options = options with { ExpectedAreaHash = 123u };
        Assert.Null(LiveTestHost.ValidateInvocation(test, options));
    }

    [Fact]
    public void RepeatablePhaseRequiresAtLeastThreeIterations()
    {
        var test = new FakeTest(LiveTestMutation.ReadOnly, drivesInput: false);
        var options = Options(LiveTestPhase.Repeatable);

        Assert.Contains("at least 3", LiveTestHost.ValidateInvocation(test, options));
        Assert.Null(LiveTestHost.ValidateInvocation(test, options with { Iterations = 3 }));
        Assert.Contains("exactly one", LiveTestHost.ValidateInvocation(
            test, options with { Phase = LiveTestPhase.Single, Iterations = 3 }));
    }

    [Fact]
    public void EveryEconomicRunRequiresExplicitCommit()
    {
        var test = new FakeTest(LiveTestMutation.Economic, drivesInput: false);

        Assert.Contains("--commit", LiveTestHost.ValidateInvocation(test, Options(LiveTestPhase.Research)));
        Assert.Contains("--commit", LiveTestHost.ValidateInvocation(test, Options(LiveTestPhase.Single)));
        Assert.Null(LiveTestHost.ValidateInvocation(
            test, Options(LiveTestPhase.Single) with { Commit = true }));
    }

    [Fact]
    public void RegistryIdsAreUniqueAndInitialGroundworkIsPresent()
    {
        var ids = LiveTestRegistry.All.Select(x => x.Id).ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("setup-inspect", ids);
        Assert.Contains("H-01-input-roundtrip", ids);
        Assert.Contains("U-02-vendor-hover-roundtrip", ids);
        Assert.Contains("U-03-vendor-page-roundtrip", ids);
        Assert.Contains("U-03-vendor-visible-offer-sweep", ids);
        Assert.Contains("U-03-vendor-page1-offer-sweep", ids);
        Assert.Contains("U-03-vendor-unavailable-offer", ids);
        Assert.Contains("U-03-vendor-affordable-offer", ids);
        Assert.Contains("A-01-nessa-shop-roundtrip", ids);
        Assert.Contains("U-01-bestel-dialog-shape", ids);
        Assert.Contains("U-05-enemy-at-gate-reward-discovery", ids);
        Assert.Contains("A-02-enemy-at-gate-reward-claim", ids);
    }

    private static LiveTestOptions Options(LiveTestPhase phase) => new(
        LiveTestCommand.Run,
        "fake",
        phase,
        Armed: false,
        Commit: false,
        SetupConfirmed: false,
        ExpectedCharacter: null,
        ExpectedAreaHash: null,
        TimeoutSeconds: 60,
        CountdownSeconds: 0,
        Iterations: 1,
        ArtifactRoot: Path.GetTempPath(),
        ExpectedReward: null);

    private sealed class FakeTest(LiveTestMutation mutation, bool drivesInput) : ILiveTestCase
    {
        public string Id => "fake";
        public string Name => "fake";
        public string Description => "fake";
        public string ManualSetup => "fake";
        public LiveTestMutation Mutation => mutation;
        public bool DrivesInput => drivesInput;

        public Task<LiveTestCaseResult> RunAsync(LiveTestContext context, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
