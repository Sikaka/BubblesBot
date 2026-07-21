using BubblesBot.Bot.Behaviors.Movement;
using BubblesBot.Bot.Input;
using BubblesBot.Bot.Settings;
using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Behaviors.Interact;

/// <summary>
/// Walk to the nearest area-transition entity (portal or zone door) and click it. Uses the
/// EntityCache for entity discovery + ground-label list for the clickable rect. Latches on
/// area change — a successful click results in <c>AreaHash</c> shifting, at which point the
/// behavior returns Success.
///
/// <para>Composes with FollowPath for movement to the transition. Click verification uses
/// "area hash changed" as the signal (the only reliable "you actually transitioned" check).</para>
///
/// <para><b>Filter.</b> The optional <paramref name="kindFilter"/> lets callers narrow which
/// transition to take — e.g. <c>e =&gt; e.Path.Contains("MapDevice")</c> for the device exit
/// or <c>e =&gt; e.Metadata.Contains("ToHideout")</c>. Default = first AreaTransition in
/// range. </para>
/// </summary>
public sealed class EnterAreaTransition : IBehavior
{
    internal const float ReliableClickRangeGrid = 10f;
    private readonly InteractSystem _interact;
    private readonly FollowPath     _approach;
    private readonly Func<Core.Snapshot.EntityCache.Entry, bool>? _kindFilter;
    private readonly Func<GameSnapshot?> _liveSnapshot;
    private readonly Func<BehaviorContext, Vector2i?>? _fallbackGrid;
    private uint _initialAreaHash;
    private Vector2i? _clickOriginGrid;
    private TimeSpan _lastClickAt = TimeSpan.MinValue;
    private bool _clickDispatched;

    public string Name { get; }
    public BehaviorStatus LastStatus { get; private set; } = BehaviorStatus.Failure;

    public EnterAreaTransition(string name, InteractSystem interact, MovementSystem movement,
        SkillBook skills, Func<GameSnapshot?> liveSnapshot,
        Func<Core.Snapshot.EntityCache.Entry, bool>? kindFilter = null,
        Func<BehaviorContext, Vector2i?>? fallbackGrid = null,
        bool allowGapCrossing = true)
    {
        Name = name;
        _interact = interact;
        _liveSnapshot = liveSnapshot;
        _kindFilter = kindFilter;
        _fallbackGrid = fallbackGrid;
        _approach = new FollowPath($"{name}/approach", movement,
            ctx => FindTransitionGrid(ctx, _kindFilter) ?? _fallbackGrid?.Invoke(ctx), skills,
            goalArrivalRadius: 8f, allowGapCrossing: allowGapCrossing);
    }

    public BehaviorStatus Tick(BehaviorContext ctx)
    {
        // Area-hash latch — a successful click flips the hash. Treat that as Success.
        if (_initialAreaHash == 0 && ctx.Snapshot.AreaHash != 0)
            _initialAreaHash = ctx.Snapshot.AreaHash;
        if (_initialAreaHash != 0
            && ctx.Snapshot.AreaHash != 0
            && ctx.Snapshot.AreaHash != _initialAreaHash)
        {
            if (!IsConfirmedAreaChange(
                    _initialAreaHash, ctx.Snapshot.AreaHash, _clickDispatched))
            {
                // Area hashes can churn briefly as top-level game state hydrates. A change
                // is only our success signal after THIS behavior dispatched a portal click.
                _initialAreaHash = ctx.Snapshot.AreaHash;
            }
            else
            {
            _initialAreaHash = ctx.Snapshot.AreaHash;
            return LastStatus = BehaviorStatus.Success;
            }
        }

        // Some map boss doors (live Strand) teleport between disconnected grid regions inside
        // the SAME area instance/hash. A click followed by a large player displacement is equally
        // strong completion evidence and prevents treating the arena exit as a retry target.
        if (_clickOriginGrid is { } origin
            && ctx.Live is { } displaced
            && IsConfirmedSameAreaTeleport(origin, displaced.GridPosition, _clickDispatched))
            return LastStatus = BehaviorStatus.Success;

        var label = FindTransitionLabel(ctx, _kindFilter);
        if (label is null)
            return LastStatus = _approach.Tick(ctx);   // can't see it yet — keep walking toward last known grid

        if (!IsInClickRange(ctx, label))
            return LastStatus = _approach.Tick(ctx);

        if (label.LabelRect is not { } rect)
            return LastStatus = _approach.Tick(ctx);

        // Throttle clicks while waiting for the loading screen / hash flip.
        if (BotMonotonicClock.ElapsedSince(_lastClickAt).TotalSeconds < 1.0)
            return LastStatus = BehaviorStatus.Running;

        var (sx, sy) = ctx.Snapshot.Window.ToScreen(rect.CenterX, rect.CenterY);
        var startHash = ctx.Snapshot.AreaHash;
        var startGrid = ctx.Live?.GridPosition;
        var ticket = ctx.Input.Click(sx, sy, ClickIntent.InteractWorld, $"transition {label.Path}",
            expectResolved: () =>
            {
                var snap = _liveSnapshot();
                return snap is null
                    || snap.AreaHash != startHash
                    || (startGrid is { } grid
                        && snap.Player is { } player
                        && IsConfirmedSameAreaTeleport(grid, player.GridPosition, clickDispatched: true));
            },
            timeoutMs: 8000);   // loading screens can be long
        if (ticket.Accepted)
        {
            _lastClickAt = BotMonotonicClock.Now;
            _clickDispatched = true;
            _clickOriginGrid = startGrid;
        }
        return LastStatus = BehaviorStatus.Running;
    }

    public void Reset()
    {
        _approach.Reset();
        _interact.Cancel();
        _initialAreaHash = 0;
        _clickOriginGrid = null;
        _lastClickAt = TimeSpan.MinValue;
        _clickDispatched = false;
        LastStatus = BehaviorStatus.Failure;
    }

    public static bool IsConfirmedAreaChange(
        uint originAreaHash, uint observedAreaHash, bool clickDispatched)
        => clickDispatched
        && originAreaHash != 0
        && observedAreaHash != 0
        && observedAreaHash != originAreaHash;

    public static bool IsConfirmedSameAreaTeleport(
        Vector2i origin, Vector2i observed, bool clickDispatched, int minimumGridDistance = 120)
    {
        if (!clickDispatched) return false;
        long dx = observed.X - origin.X, dy = observed.Y - origin.Y;
        return dx * dx + dy * dy >= (long)minimumGridDistance * minimumGridDistance;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static Vector2i? FindTransitionGrid(BehaviorContext ctx, Func<Core.Snapshot.EntityCache.Entry, bool>? filter)
    {
        if (ctx.Entities is null || ctx.Live is null) return null;
        var p = ctx.Live.Value.GridPosition;
        Core.Snapshot.EntityCache.Entry? best = null; long bestD2 = long.MaxValue;
        foreach (var e in ctx.Entities.Entries.Values)
        {
            // Cached stale transitions remain valid NAVIGATION anchors. FindTransitionLabel
            // separately requires freshness before authorizing the actual click.
            if (e.Kind != Core.Game.EntityListReader.EntityKind.AreaTransition
             && e.Kind != Core.Game.EntityListReader.EntityKind.Portal
             && e.Kind != Core.Game.EntityListReader.EntityKind.TownPortal) continue;
            if (filter is not null && !filter(e)) continue;
            long dx = e.GridPosition.X - p.X, dy = e.GridPosition.Y - p.Y;
            var d2 = dx * dx + dy * dy;
            if (d2 < bestD2) { bestD2 = d2; best = e; }
        }
        return best?.GridPosition;
    }

    private static GroundLabelView? FindTransitionLabel(BehaviorContext ctx, Func<Core.Snapshot.EntityCache.Entry, bool>? filter)
    {
        // Ground labels include AreaTransition entities. We find the nearest one whose
        // backing entity matches the filter.
        if (ctx.Entities is null) return null;
        GroundLabelView? best = null; float bestD2 = float.PositiveInfinity;
        foreach (var entity in ctx.Entities.Entries.Values)
        {
            if (entity.IsStale) continue;
            if (entity.Kind != Core.Game.EntityListReader.EntityKind.AreaTransition
             && entity.Kind != Core.Game.EntityListReader.EntityKind.Portal
             && entity.Kind != Core.Game.EntityListReader.EntityKind.TownPortal) continue;
            if (filter is not null && !filter(entity)) continue;
            foreach (var label in ctx.Snapshot.GroundLabels)
            {
                if (label.EntityId != entity.Id || label.EntityGridPosition is not { } g) continue;
                var dx = (float)(g.X - (ctx.Live?.GridPosition.X ?? 0));
                var dy = (float)(g.Y - (ctx.Live?.GridPosition.Y ?? 0));
                var d2 = dx * dx + dy * dy;
                if (d2 < bestD2) { bestD2 = d2; best = label; }
            }
        }
        return best;
    }

    private static bool IsInClickRange(BehaviorContext ctx, GroundLabelView label)
    {
        if (label.EntityGridPosition is not { } g || ctx.Live is null) return false;
        var p = ctx.Live.Value.GridPosition;
        return IsWithinReliableClickRange(p, g, ctx.Settings.InteractionRangeGrid);
    }

    internal static bool IsWithinReliableClickRange(
        Vector2i player, Vector2i transition, float configuredRange)
    {
        var dx = (float)(transition.X - player.X);
        var dy = (float)(transition.Y - player.Y);
        var r = Math.Min(configuredRange, ReliableClickRangeGrid);
        return dx * dx + dy * dy <= r * r;
    }
}
