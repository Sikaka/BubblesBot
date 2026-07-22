using BubblesBot.Bot.Systems;

namespace BubblesBot.Tests;

public sealed class AreaTransitionTrackerTests
{
    [Fact]
    public void HashChangeWithoutDestinationEvidenceIsNotSuccess()
    {
        var tracker = new AreaTransitionTracker(TimeSpan.FromSeconds(5));
        tracker.Start(10, AreaRole.Map, AreaRole.SafeHub, TimeSpan.Zero);

        var state = tracker.Observe(20, AreaRole.Unknown, TimeSpan.FromSeconds(1));

        Assert.Equal(AreaTransitionOutcome.VerifyingDestination, state.Outcome);
    }

    [Fact]
    public void ExpectedDestinationConfirmsTransition()
    {
        var tracker = new AreaTransitionTracker();
        tracker.Start(10, AreaRole.Map, AreaRole.SafeHub, TimeSpan.Zero);

        var state = tracker.Observe(20, AreaRole.SafeHub, TimeSpan.FromSeconds(1));

        Assert.Equal(AreaTransitionOutcome.Confirmed, state.Outcome);
        Assert.Equal(20u, state.ObservedAreaHash);
    }

    [Fact]
    public void KnownWrongDestinationFailsImmediately()
    {
        var tracker = new AreaTransitionTracker();
        tracker.Start(10, AreaRole.Map, AreaRole.SafeHub, TimeSpan.Zero);

        var state = tracker.Observe(20, AreaRole.Map, TimeSpan.FromSeconds(1));

        Assert.Equal(AreaTransitionOutcome.UnexpectedDestination, state.Outcome);
    }

    [Fact]
    public void UnknownDestinationTimesOutDeterministically()
    {
        var tracker = new AreaTransitionTracker(TimeSpan.FromSeconds(5));
        tracker.Start(10, AreaRole.Map, AreaRole.SafeHub, TimeSpan.Zero);
        tracker.Observe(20, AreaRole.Unknown, TimeSpan.FromSeconds(1));

        var state = tracker.Observe(20, AreaRole.Unknown, TimeSpan.FromSeconds(6));

        Assert.Equal(AreaTransitionOutcome.TimedOut, state.Outcome);
    }

    [Fact]
    public void AdditionalEvidenceTime_ExtendsOnlyTheUnknownDestinationDeadline()
    {
        var tracker = new AreaTransitionTracker(TimeSpan.FromSeconds(5));
        tracker.Start(10, AreaRole.Map, AreaRole.SafeHub, TimeSpan.Zero);
        tracker.Observe(20, AreaRole.Unknown, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));

        var stillWaiting = tracker.Observe(
            20, AreaRole.Unknown, TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(3));
        var timedOut = tracker.Observe(
            20, AreaRole.Unknown, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(3));

        Assert.Equal(AreaTransitionOutcome.VerifyingDestination, stillWaiting.Outcome);
        Assert.Equal(AreaTransitionOutcome.TimedOut, timedOut.Outcome);
    }
}
