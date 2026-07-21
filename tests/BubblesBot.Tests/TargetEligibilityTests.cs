using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;
using BubblesBot.Core.Knowledge;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Tests;

public sealed class TargetEligibilityTests
{
    [Fact]
    public void FullyObservedCombatantIsEligible()
        => Assert.True(TargetEligibility.Evaluate(Eligible()).Accepted);

    [Theory]
    [InlineData("reaction", TargetRejectionReason.ReactionUnknown)]
    [InlineData("life", TargetRejectionReason.LifeUnknown)]
    [InlineData("targetable", TargetRejectionReason.TargetabilityUnknown)]
    [InlineData("dormancy", TargetRejectionReason.DormancyUnknown)]
    public void UnknownCriticalObservationFailsClosed(string field, TargetRejectionReason expected)
    {
        var entity = Eligible();
        var unknown = BooleanObservation.Unknown(field, 2, ObservationReadStatus.ReadFailed);
        switch (field)
        {
            case "reaction": entity.AlliedReaction = unknown; break;
            case "life": entity.LifeReadable = unknown; break;
            case "targetable": entity.Targetability = unknown; break;
            case "dormancy": entity.Dormancy = unknown; break;
        }

        var result = TargetEligibility.Evaluate(entity);
        Assert.False(result.Accepted);
        Assert.Equal(expected, result.Reason);
    }

    [Fact]
    public void DormantTargetIsRejected()
    {
        var entity = Eligible();
        entity.Dormancy = Known(true);
        Assert.Equal(TargetRejectionReason.Dormant, TargetEligibility.Evaluate(entity).Reason);
    }

    [Fact]
    public void StaleTargetIsRejectedUnlessExplicitlyAllowed()
    {
        var entity = Eligible();
        entity.MissedWalks = 1;
        Assert.Equal(TargetRejectionReason.Stale, TargetEligibility.Evaluate(entity).Reason);
        Assert.True(TargetEligibility.Evaluate(entity, allowStale: true).Accepted);
    }

    private static EntityCache.Entry Eligible() => new()
    {
        Id = 42,
        Kind = EntityListReader.EntityKind.Monster,
        Disposition = EntityDisposition.Combatant,
        HpCurrent = 100,
        HpMax = 100,
        AlliedReaction = Known(false),
        LifeReadable = Known(true),
        Targetability = Known(true),
        Dormancy = Known(false),
    };

    private static BooleanObservation Known(bool value)
        => BooleanObservation.Known(value, "test", 1, ObservationConfidence.Validated);
}
