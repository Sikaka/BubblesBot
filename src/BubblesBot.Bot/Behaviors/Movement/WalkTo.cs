using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;

namespace BubblesBot.Bot.Behaviors.Movement;

/// <summary>
/// Walk to a grid cell using <see cref="MovementSystem"/>. The target is recomputed each tick
/// via the supplied <paramref name="targetSelector"/> — pass a lambda that re-reads the world
/// state so the behavior tracks moving targets (chasing a fleeing mob, following a label that
/// shifts with player movement).
///
/// <para>Returns Success when within <c>Settings.ArrivalRadiusGrid</c>; Running otherwise.
/// Returns Failure only when the selector yields no target.</para>
/// </summary>
public sealed class WalkTo : IBehavior
{
    private readonly MovementSystem _movement;
    private readonly Func<BehaviorContext, Vector2i?> _targetSelector;
    private readonly float? _arrivalRadiusOverride;

    public string Name { get; }
    public BehaviorStatus LastStatus { get; private set; } = BehaviorStatus.Failure;

    public WalkTo(string name, MovementSystem movement, Func<BehaviorContext, Vector2i?> targetSelector, float? arrivalRadius = null)
    {
        Name = name;
        _movement = movement;
        _targetSelector = targetSelector;
        _arrivalRadiusOverride = arrivalRadius;
    }

    public BehaviorStatus Tick(BehaviorContext ctx)
    {
        var target = _targetSelector(ctx);
        if (target is null) { _movement.Release(this); return LastStatus = BehaviorStatus.Failure; }

        var live = ctx.Live;
        if (live is null) { _movement.Release(this); return LastStatus = BehaviorStatus.Failure; }

        var dx = (float)(target.Value.X - live.Value.GridPosition.X);
        var dy = (float)(target.Value.Y - live.Value.GridPosition.Y);
        var dist = MathF.Sqrt(dx * dx + dy * dy);

        var arrival = _arrivalRadiusOverride ?? ctx.Settings.ArrivalRadiusGrid;
        if (dist <= arrival)
        {
            _movement.Halt(new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live), this);
            return LastStatus = BehaviorStatus.Success;
        }

        _movement.WalkToward(target.Value, new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live), this);
        return LastStatus = BehaviorStatus.Running;
    }

    public void Reset() { _movement.Release(this); LastStatus = BehaviorStatus.Failure; }
}

/// <summary>
/// Halt motion immediately. Always returns Success after issuing the stop. Use as a guard at
/// the end of a chain that needs to stand still (e.g. before clicking loot or casting a
/// stationary skill).
/// </summary>
public sealed class StopMoving : IBehavior
{
    private readonly MovementSystem _movement;
    public string Name { get; }
    public BehaviorStatus LastStatus { get; private set; } = BehaviorStatus.Success;

    public StopMoving(string name, MovementSystem movement) { Name = name; _movement = movement; }

    public BehaviorStatus Tick(BehaviorContext ctx)
    {
        _movement.Halt(new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live));
        return LastStatus = BehaviorStatus.Success;
    }
}
