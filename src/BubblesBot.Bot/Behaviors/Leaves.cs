namespace BubblesBot.Bot.Behaviors;

/// <summary>Inline action: a one-tick lambda that returns Success/Failure/Running.</summary>
public sealed class Action : IBehavior
{
    private readonly Func<BehaviorContext, BehaviorStatus> _fn;
    public string Name { get; }
    public BehaviorStatus LastStatus { get; private set; } = BehaviorStatus.Failure;

    public Action(string name, Func<BehaviorContext, BehaviorStatus> fn)
    {
        Name = name;
        _fn  = fn;
    }

    public BehaviorStatus Tick(BehaviorContext ctx) => LastStatus = _fn(ctx);
}

/// <summary>Inline predicate: Success when true, Failure when false.</summary>
public sealed class Condition : IBehavior
{
    private readonly Func<BehaviorContext, bool> _pred;
    public string Name { get; }
    public BehaviorStatus LastStatus { get; private set; } = BehaviorStatus.Failure;

    public Condition(string name, Func<BehaviorContext, bool> pred)
    {
        Name = name;
        _pred = pred;
    }

    public BehaviorStatus Tick(BehaviorContext ctx)
        => LastStatus = _pred(ctx) ? BehaviorStatus.Success : BehaviorStatus.Failure;
}
