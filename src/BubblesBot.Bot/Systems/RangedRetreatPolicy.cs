namespace BubblesBot.Bot.Systems;

/// <summary>Pure policy seam for the attack leg between ranged reposition casts.</summary>
public static class RangedRetreatPolicy
{
    public static bool ShouldAttackWhileDashRecharges(
        float nearestThreatDistance,
        float attackRange,
        float configuredStandoff)
    {
        if (!float.IsFinite(nearestThreatDistance)
            || !float.IsFinite(attackRange)
            || attackRange <= 0)
            return false;

        // Do not plant while the nearest hostile is still in melee space. A low configured
        // standoff is clamped so a malformed profile cannot turn the retreat into face-tanking.
        var minimumSafeDistance = Math.Clamp(configuredStandoff * 0.55f, 20f, 32f);
        return nearestThreatDistance >= minimumSafeDistance
            && nearestThreatDistance <= attackRange;
    }
}
