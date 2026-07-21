using BubblesBot.Core.Game;
using BubblesBot.Core.Knowledge;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Systems;

public enum TargetRejectionReason
{
    None,
    NotMonster,
    NonCombatantDisposition,
    Stale,
    ReactionUnknown,
    Allied,
    LifeUnknown,
    Dead,
    TargetabilityUnknown,
    NotTargetable,
    DormancyUnknown,
    Dormant,
    IgnoredName,
}

public readonly record struct TargetEligibilityResult(bool Accepted, TargetRejectionReason Reason)
{
    public static TargetEligibilityResult Accept() => new(true, TargetRejectionReason.None);
    public static TargetEligibilityResult Reject(TargetRejectionReason reason) => new(false, reason);
}

public readonly record struct TargetFacts(
    EntityListReader.EntityKind Kind,
    EntityDisposition Disposition,
    bool IsStale,
    ObservationTruth AlliedReaction,
    ObservationTruth LifeReadable,
    int HpCurrent,
    int HpMax,
    ObservationTruth Targetability,
    ObservationTruth Dormancy,
    string Name);

/// <summary>Single fail-closed combat target policy used by every production selector.</summary>
public static class TargetEligibility
{
    public static TargetEligibilityResult Evaluate(EntityCache.Entry e, bool allowStale = false)
        => Evaluate(new TargetFacts(
            e.Kind, e.Disposition, e.IsStale,
            e.AlliedReaction.Truth, e.LifeReadable.Truth, e.HpCurrent, e.HpMax,
            e.Targetability.Truth, e.Dormancy.Truth, e.Name), allowStale);

    public static TargetEligibilityResult Evaluate(TargetFacts e, bool allowStale = false)
    {
        if (e.Kind != EntityListReader.EntityKind.Monster)
            return TargetEligibilityResult.Reject(TargetRejectionReason.NotMonster);
        if (e.Disposition != EntityDisposition.Combatant)
            return TargetEligibilityResult.Reject(TargetRejectionReason.NonCombatantDisposition);
        if (!allowStale && e.IsStale)
            return TargetEligibilityResult.Reject(TargetRejectionReason.Stale);
        if (e.AlliedReaction == ObservationTruth.Unknown)
            return TargetEligibilityResult.Reject(TargetRejectionReason.ReactionUnknown);
        if (e.AlliedReaction == ObservationTruth.True)
            return TargetEligibilityResult.Reject(TargetRejectionReason.Allied);
        if (e.LifeReadable == ObservationTruth.Unknown)
            return TargetEligibilityResult.Reject(TargetRejectionReason.LifeUnknown);
        if (e.HpCurrent <= 0 || e.HpMax <= 0)
            return TargetEligibilityResult.Reject(TargetRejectionReason.Dead);
        if (e.Targetability == ObservationTruth.Unknown)
            return TargetEligibilityResult.Reject(TargetRejectionReason.TargetabilityUnknown);
        if (e.Targetability != ObservationTruth.True)
            return TargetEligibilityResult.Reject(TargetRejectionReason.NotTargetable);
        if (e.Dormancy == ObservationTruth.Unknown)
            return TargetEligibilityResult.Reject(TargetRejectionReason.DormancyUnknown);
        if (e.Dormancy == ObservationTruth.True)
            return TargetEligibilityResult.Reject(TargetRejectionReason.Dormant);
        if (EnemyIgnoreList.IsIgnored(e.Name))
            return TargetEligibilityResult.Reject(TargetRejectionReason.IgnoredName);
        return TargetEligibilityResult.Accept();
    }

    public static bool IsEligible(EntityCache.Entry e, bool allowStale = false)
        => Evaluate(e, allowStale).Accepted;

    public static bool IsEligible(TargetFacts e, bool allowStale = false)
        => Evaluate(e, allowStale).Accepted;
}
