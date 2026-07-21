using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;

namespace BubblesBot.Bot.Behaviors.Movement;

/// <summary>
/// Pick the next frontier target via <see cref="ExplorationSystem"/> and walk to it via
/// <see cref="FollowPath"/>. Composable as a low-priority branch in any mode tree:
/// "if nothing else to do, explore." Returns Failure when no frontier exists (map fully
/// explored), letting the parent Selector advance to "go to map exit."
/// </summary>
public sealed class ExploreFrontier : IBehavior
{
    private readonly ExplorationSystem _exploration;
    private readonly FollowPath _follow;

    public string Name { get; }
    public BehaviorStatus LastStatus { get; private set; } = BehaviorStatus.Failure;

    /// <summary>The underlying path-follower — exposed so modes can surface goal/path/decision
    /// telemetry on the dashboard.</summary>
    public FollowPath Follow => _follow;

    public ExploreFrontier(string name, ExplorationSystem exploration, MovementSystem movement, SkillBook skills)
    {
        Name = name;
        _exploration = exploration;
        _follow = new FollowPath($"{name}/follow", movement, ctx => _exploration.PickFrontier(ctx), skills,
            // Frontier doesn't need to land precisely — generous arrival keeps us moving forward.
            goalArrivalRadius: 12f);
    }

    public BehaviorStatus Tick(BehaviorContext ctx)
    {
        var live = ctx.Live;
        if (live is not null) _exploration.TrackVisit(ctx.Snapshot, live.Value.GridPosition);
        return LastStatus = _follow.Tick(ctx);
    }

    public void Reset() { _follow.Reset(); LastStatus = BehaviorStatus.Failure; }

    public void Visit(IBehaviorVisitor v)
    {
        v.Node(Name, LastStatus, 1);
        v.Down(); ((IBehavior)_follow).Visit(v); v.Up();
    }
}
