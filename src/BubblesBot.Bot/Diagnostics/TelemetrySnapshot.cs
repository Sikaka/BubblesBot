using BubblesBot.Bot.Behaviors;
using BubblesBot.Bot.Behaviors.Movement;
using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;
using BubblesBot.Core.Knowledge;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Diagnostics;

/// <summary>
/// Builders for the per-tick diagnostic blocks that map-clearing modes publish to the
/// status feed (the dashboard's <c>farm</c> key). Everything here is read-only over the
/// entity cache / follow-path state; modes call these once per world tick and swap the
/// result in as a single reference so the web broadcast thread never sees a torn object.
/// </summary>
public static class TelemetrySnapshot
{
    /// <summary>Typed census so modes can drive both the JSON status feed and the overlay
    /// HUD from one pass. Serializes to camelCase like the anonymous objects around it.</summary>
    public sealed record CensusResult(
        int Entities, int HostileAlive, int Targetable, int Untargetable, int StaleHostiles,
        int Hazards, int NearestHazard, int Allies, int Dormant, string NearestTargetable, string NearestGhost,
        IReadOnlyList<object> Nearby, IReadOnlyDictionary<string, int> Rejections);

    /// <summary>
    /// One pass over the entity cache: hostile split (alive vs targetable vs stale — the
    /// primary "mob exists in memory but isn't really spawned" diagnostic) plus the 8
    /// nearest hostiles with identity, so a stalled bot's orbit target can be named.
    /// </summary>
    public static CensusResult EntityCensus(BehaviorContext ctx)
    {
        int total = 0, hostileAlive = 0, targetable = 0, staleHostiles = 0, hazards = 0, allies = 0, dormant = 0;
        var nearby = new List<object>(8);
        EntityCache.Entry? nearestTargetable = null, nearestGhost = null;
        float nearestTargetableD = float.PositiveInfinity, nearestGhostD = float.PositiveInfinity;
        float nearestHazardD = float.PositiveInfinity;
        var rejections = new Dictionary<string, int>(StringComparer.Ordinal);

        if (ctx.Entities is not null && ctx.Live is { } lv)
        {
            var p = lv.GridPosition;
            var hostiles = new List<(EntityCache.Entry e, float d)>();
            foreach (var e in ctx.Entities.Entries.Values)
            {
                total++;
                if (e.AlliedReaction.IsTrue && e.Kind == EntityListReader.EntityKind.Monster && e.IsAlive) allies++;
                if (e.IsHazard && e.IsAlive && !e.IsStale)
                {
                    hazards++;
                    float hdx = e.GridPosition.X - p.X, hdy = e.GridPosition.Y - p.Y;
                    var hd = MathF.Sqrt(hdx * hdx + hdy * hdy);
                    if (hd < nearestHazardD) nearestHazardD = hd;
                }
                if (e.Kind != EntityListReader.EntityKind.Monster
                    || e.Disposition != EntityDisposition.Combatant
                    || !e.IsAlive) continue;
                hostileAlive++;
                if (e.IsStale) staleHostiles++;
                if (e.IsDormant) dormant++;
                float dx = e.GridPosition.X - p.X, dy = e.GridPosition.Y - p.Y;
                var d = MathF.Sqrt(dx * dx + dy * dy);
                hostiles.Add((e, d));
                var eligibility = TargetEligibility.Evaluate(e);
                if (eligibility.Accepted)
                {
                    targetable++;
                    if (d < nearestTargetableD) { nearestTargetableD = d; nearestTargetable = e; }
                }
                else
                {
                    var reason = eligibility.Reason.ToString();
                    rejections[reason] = rejections.GetValueOrDefault(reason) + 1;
                    if (d < nearestGhostD) { nearestGhostD = d; nearestGhost = e; }
                }
            }
            hostiles.Sort((a, b) => a.d.CompareTo(b.d));
            foreach (var (e, d) in hostiles.Take(8))
                nearby.Add(new
                {
                    id    = e.Id,
                    name  = ShortName(e),
                    dist  = (int)d,
                    hp    = e.HpCurrent,
                    hpMax = e.HpMax,
                    targetable = e.Targetability.Truth.ToString(),
                    dormant    = e.Dormancy.Truth.ToString(),
                    reaction   = e.AlliedReaction.Truth.ToString(),
                    life       = e.LifeReadable.Truth.ToString(),
                    eligible   = TargetEligibility.Evaluate(e).Accepted,
                    rejection  = TargetEligibility.Evaluate(e).Reason.ToString(),
                    moving     = e.IsMoving,
                    stale      = e.IsStale,
                });
        }

        return new CensusResult(
            Entities:      total,
            HostileAlive:  hostileAlive,
            Targetable:    targetable,
            Untargetable:  hostileAlive - targetable,
            StaleHostiles: staleHostiles,
            Hazards:       hazards,
            NearestHazard: float.IsFinite(nearestHazardD) ? (int)nearestHazardD : -1,
            Allies:        allies,
            Dormant:       dormant,
            NearestTargetable: nearestTargetable is { } nt
                ? $"{ShortName(nt)}#{nt.Id}@{(int)nearestTargetableD}" : "",
            NearestGhost: nearestGhost is { } ng
                ? $"{ShortName(ng)}#{ng.Id}@{(int)nearestGhostD}" : "",
            Nearby: nearby,
            Rejections: rejections);
    }

    /// <summary>Frontier-walk progress from the exploration follow-path.</summary>
    public static object ExploreState(FollowPath follow, ExplorationSystem? exploration = null) => new
    {
        goalX     = follow.Goal?.X ?? 0,
        goalY     = follow.Goal?.Y ?? 0,
        pathLen   = follow.CurrentPath?.Count ?? 0,
        pathIndex = follow.CurrentPathIndex,
        blinks    = follow.BlinksFired,
        decision  = follow.LastDecision,
        frontierScore = exploration?.LastFrontierScore ?? 0,
        frontierReason = exploration?.LastFrontierReason ?? "",
    };

    /// <summary>Display name when present, else the last metadata path segment.</summary>
    public static string ShortName(EntityCache.Entry e)
    {
        if (!string.IsNullOrEmpty(e.Name)) return e.Name;
        var m = e.Metadata;
        var i = m.LastIndexOf('/');
        return i >= 0 && i < m.Length - 1 ? m[(i + 1)..] : m;
    }
}
