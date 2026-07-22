using BubblesBot.Bot.Behaviors;
using BubblesBot.Bot.Input;
using BubblesBot.Bot.Settings;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Systems;

/// <summary>
/// Casts and positively confirms a Portal Scroll near a boss-arena entrance without entering it.
/// The portal is a death checkpoint; successful map completion still uses LeaveMapSystem from
/// the boss/reward location.
/// </summary>
public sealed class BossCheckpointPortalSystem
{
    private readonly MovementSystem _movement;
    private readonly Func<int> _getPortalVk;
    private readonly Func<EntityCache?> _getEntities;
    private readonly Func<LivePlayer?> _getLive;
    private TimeSpan _startedAt = TimeSpan.MinValue;
    private TimeSpan _lastActionAt = TimeSpan.MinValue;
    private int _attempts;

    private const int MaxAttempts = 4;
    private const int TimeoutSeconds = 15;
    private const int ActionCooldownMs = 600;
    internal const float ConfirmationRangeGrid = 30f;

    public bool IsReady { get; private set; }
    public bool IsFailed { get; private set; }
    public string Status { get; private set; } = "idle";

    public BossCheckpointPortalSystem(
        MovementSystem movement,
        Func<int> getPortalVk,
        Func<EntityCache?> getEntities,
        Func<LivePlayer?> getLive)
    {
        _movement = movement;
        _getPortalVk = getPortalVk;
        _getEntities = getEntities;
        _getLive = getLive;
    }

    public BehaviorStatus Tick(BehaviorContext ctx)
    {
        if (IsReady) return BehaviorStatus.Success;
        if (IsFailed) return BehaviorStatus.Failure;

        _movement.Halt(new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live));
        var now = BotMonotonicClock.Now;
        if (_startedAt == TimeSpan.MinValue) _startedAt = now;

        if (HasUsablePortal(ctx.Entities, ctx.Live))
        {
            IsReady = true;
            Status = "boss checkpoint portal confirmed";
            Diagnostics.EventLog.Emit(
                "maprun", "maprun.boss-checkpoint-ready", Diagnostics.EventSeverity.Info,
                Status,
                new Dictionary<string, object?>
                {
                    ["attempts"] = _attempts,
                    ["areaHash"] = $"0x{ctx.Snapshot.AreaHash:X8}",
                    ["gridX"] = ctx.Live?.GridPosition.X,
                    ["gridY"] = ctx.Live?.GridPosition.Y,
                });
            return BehaviorStatus.Success;
        }

        var maxAttempts = LatencyPolicy.RetryLimit(MaxAttempts, ctx.Settings);
        if (_attempts >= maxAttempts || (now - _startedAt).TotalSeconds
            >= LatencyPolicy.TimeoutSeconds(TimeoutSeconds, ctx.Settings))
        {
            IsFailed = true;
            Status = $"boss checkpoint portal unavailable after {_attempts} attempts";
            Diagnostics.EventLog.Emit(
                "maprun", "maprun.boss-checkpoint-failed", Diagnostics.EventSeverity.Warning,
                Status);
            return BehaviorStatus.Failure;
        }

        if (_lastActionAt != TimeSpan.MinValue
            && (now - _lastActionAt).TotalMilliseconds < ActionCooldownMs)
            return BehaviorStatus.Running;

        var vk = _getPortalVk();
        if (vk == 0)
        {
            IsFailed = true;
            Status = "boss checkpoint portal key is unbound";
            Diagnostics.EventLog.Emit(
                "maprun", "maprun.boss-checkpoint-failed", Diagnostics.EventSeverity.Warning,
                Status);
            return BehaviorStatus.Failure;
        }

        var ticket = ctx.Input.VerifiedTapKey(
            vk, ClickIntent.UseSkill, "place boss checkpoint portal",
            expectResolved: () => HasUsablePortal(_getEntities(), _getLive()),
            timeoutMs: 2500);
        if (ticket.Accepted)
        {
            _attempts++;
            _lastActionAt = now;
            Status = $"placing boss checkpoint portal ({_attempts}/{maxAttempts})";
        }
        return BehaviorStatus.Running;
    }

    internal static bool HasUsablePortal(EntityCache? entities, LivePlayer? live)
    {
        if (entities is null || live is null) return false;
        return HasUsablePortal(entities.Entries.Values, live.Value.GridPosition);
    }

    internal static bool HasUsablePortal(
        IEnumerable<EntityCache.Entry> entities, Vector2i player)
    {
        foreach (var entity in entities)
        {
            if (entity.IsStale || entity.Kind != EntityListReader.EntityKind.TownPortal)
                continue;
            long dx = entity.GridPosition.X - player.X;
            long dy = entity.GridPosition.Y - player.Y;
            if (dx * dx + dy * dy <= ConfirmationRangeGrid * ConfirmationRangeGrid)
                return true;
        }
        return false;
    }

    public void Reset()
    {
        IsReady = false;
        IsFailed = false;
        Status = "idle";
        _startedAt = TimeSpan.MinValue;
        _lastActionAt = TimeSpan.MinValue;
        _attempts = 0;
    }
}
