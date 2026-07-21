using BubblesBot.Bot.Systems;

namespace BubblesBot.Tests;

public sealed class DamageEvidenceTrackerTests
{
    [Fact]
    public void DoesNotBlacklistBeforeEvidenceWindow()
    {
        var tracker = new DamageEvidenceTracker();
        tracker.ObserveAcceptedAttack(7, 100, TimeSpan.Zero, 1200, 15000);

        var result = tracker.ObserveAcceptedAttack(7, 100, TimeSpan.FromMilliseconds(1199), 1200, 15000);

        Assert.Equal(DamageEvidenceOutcome.Waiting, result.Outcome);
        Assert.False(tracker.IsBlacklisted(7, TimeSpan.FromMilliseconds(1199)));
    }

    [Fact]
    public void HpDropRestartsEvidenceWindow()
    {
        var tracker = new DamageEvidenceTracker();
        tracker.ObserveAcceptedAttack(7, 100, TimeSpan.Zero, 1200, 15000);
        var damage = tracker.ObserveAcceptedAttack(7, 90, TimeSpan.FromMilliseconds(1000), 1200, 15000);
        var waiting = tracker.ObserveAcceptedAttack(7, 90, TimeSpan.FromMilliseconds(2000), 1200, 15000);

        Assert.Equal(DamageEvidenceOutcome.DamageObserved, damage.Outcome);
        Assert.Equal(DamageEvidenceOutcome.Waiting, waiting.Outcome);
    }

    [Fact]
    public void RepeatedAcceptedAttacksWithoutDamageBlacklistTarget()
    {
        var tracker = new DamageEvidenceTracker();
        tracker.ObserveAcceptedAttack(7, 100, TimeSpan.Zero, 1200, 15000);

        var result = tracker.ObserveAcceptedAttack(7, 100, TimeSpan.FromMilliseconds(1200), 1200, 15000);

        Assert.Equal(DamageEvidenceOutcome.Blacklisted, result.Outcome);
        Assert.True(tracker.IsBlacklisted(7, TimeSpan.FromMilliseconds(1200)));
        Assert.False(tracker.IsBlacklisted(7, TimeSpan.FromMilliseconds(16201)));
    }

    [Fact]
    public void SwitchingTargetsStartsIndependentEvidence()
    {
        var tracker = new DamageEvidenceTracker();
        tracker.ObserveAcceptedAttack(7, 100, TimeSpan.Zero, 1200, 15000);

        var result = tracker.ObserveAcceptedAttack(8, 100, TimeSpan.FromMilliseconds(1199), 1200, 15000);

        Assert.Equal(DamageEvidenceOutcome.Started, result.Outcome);
        Assert.Equal(8u, tracker.EngagedId);
    }
}
