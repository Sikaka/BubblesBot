using BubblesBot.Bot.Systems;

namespace BubblesBot.Bot.Behaviors;

/// <summary>
/// Run children in order. First Failure short-circuits → Failure. First Running short-circuits
/// → Running. All Success → Success. Standard "AND" chain — every step must succeed for the
/// whole thing to succeed. Re-evaluates from index 0 every tick (re-entrant).
/// </summary>
public sealed class Sequence : IBehavior
{
    private readonly IBehavior[] _children;
    public string Name { get; }
    public BehaviorStatus LastStatus { get; private set; } = BehaviorStatus.Failure;

    public Sequence(string name, params IBehavior[] children)
    {
        Name = name;
        _children = children;
    }

    public BehaviorStatus Tick(BehaviorContext ctx)
    {
        for (var i = 0; i < _children.Length; i++)
        {
            var s = _children[i].Tick(ctx);
            if (s == BehaviorStatus.Running) { ResetAfter(i + 1); return LastStatus = BehaviorStatus.Running; }
            if (s == BehaviorStatus.Failure) { ResetAfter(i + 1); return LastStatus = BehaviorStatus.Failure; }
        }
        return LastStatus = BehaviorStatus.Success;
    }

    private void ResetAfter(int idx)
    {
        for (var i = idx; i < _children.Length; i++) _children[i].Reset();
    }

    public void Reset() { foreach (var c in _children) c.Reset(); LastStatus = BehaviorStatus.Failure; }

    public void Visit(IBehaviorVisitor v)
    {
        v.Node(Name, LastStatus, _children.Length);
        v.Down();
        foreach (var c in _children) c.Visit(v);
        v.Up();
    }
}

/// <summary>
/// Run children in order. First Success short-circuits → Success. First Running short-circuits
/// → Running. All Failure → Failure. Standard "OR" chain — pick the first option that works.
/// </summary>
public sealed class Selector : IBehavior
{
    private readonly IBehavior[] _children;
    public string Name { get; }
    public BehaviorStatus LastStatus { get; private set; } = BehaviorStatus.Failure;

    public Selector(string name, params IBehavior[] children)
    {
        Name = name;
        _children = children;
    }

    public BehaviorStatus Tick(BehaviorContext ctx)
    {
        for (var i = 0; i < _children.Length; i++)
        {
            var s = _children[i].Tick(ctx);
            if (s == BehaviorStatus.Success) { ResetAfter(i + 1); return LastStatus = BehaviorStatus.Success; }
            if (s == BehaviorStatus.Running) { ResetAfter(i + 1); return LastStatus = BehaviorStatus.Running; }
        }
        return LastStatus = BehaviorStatus.Failure;
    }

    private void ResetAfter(int idx)
    {
        for (var i = idx; i < _children.Length; i++) _children[i].Reset();
    }

    public void Reset() { foreach (var c in _children) c.Reset(); LastStatus = BehaviorStatus.Failure; }

    public void Visit(IBehaviorVisitor v)
    {
        v.Node(Name, LastStatus, _children.Length);
        v.Down();
        foreach (var c in _children) c.Visit(v);
        v.Up();
    }
}

/// <summary>
/// Tick all children every frame. Aggregation policy: <see cref="Policy.RequireAll"/> succeeds
/// only when every child succeeds; <see cref="Policy.RequireOne"/> succeeds when any child does.
/// Failure of any child fails the parallel.
///
/// <para>Use case: combat tree (movement + buff maintenance + skill cast) — they all run
/// each tick, none waits for the others.</para>
/// </summary>
public sealed class Parallel : IBehavior
{
    public enum Policy { RequireAll, RequireOne }

    private readonly Policy _policy;
    private readonly IBehavior[] _children;
    public string Name { get; }
    public BehaviorStatus LastStatus { get; private set; } = BehaviorStatus.Failure;

    public Parallel(string name, Policy policy, params IBehavior[] children)
    {
        Name = name;
        _policy = policy;
        _children = children;
    }

    public BehaviorStatus Tick(BehaviorContext ctx)
    {
        var success = 0;
        var failed = false;
        foreach (var c in _children)
        {
            var s = c.Tick(ctx);
            if (s == BehaviorStatus.Success) success++;
            else if (s == BehaviorStatus.Failure) failed = true;
        }
        if (failed) return LastStatus = BehaviorStatus.Failure;
        var done = _policy == Policy.RequireAll ? success == _children.Length : success > 0;
        return LastStatus = done ? BehaviorStatus.Success : BehaviorStatus.Running;
    }

    public void Reset() { foreach (var c in _children) c.Reset(); LastStatus = BehaviorStatus.Failure; }

    public void Visit(IBehaviorVisitor v)
    {
        v.Node($"{Name} ({_policy})", LastStatus, _children.Length);
        v.Down();
        foreach (var c in _children) c.Visit(v);
        v.Up();
    }
}

/// <summary>If predicate true → tick child; else Failure. Used as a guard in Selector chains.</summary>
public sealed class If : IBehavior
{
    private readonly Func<BehaviorContext, bool> _pred;
    private readonly IBehavior _child;
    public string Name { get; }
    public BehaviorStatus LastStatus { get; private set; } = BehaviorStatus.Failure;

    public If(string name, Func<BehaviorContext, bool> pred, IBehavior child)
    {
        Name = name; _pred = pred; _child = child;
    }

    public BehaviorStatus Tick(BehaviorContext ctx)
    {
        if (!_pred(ctx)) { _child.Reset(); return LastStatus = BehaviorStatus.Failure; }
        return LastStatus = _child.Tick(ctx);
    }

    public void Reset() { _child.Reset(); LastStatus = BehaviorStatus.Failure; }

    public void Visit(IBehaviorVisitor v)
    {
        v.Node(Name, LastStatus, 1);
        v.Down();
        _child.Visit(v);
        v.Up();
    }
}

/// <summary>
/// Throttle a child: after a child tick that wasn't Running, refuse to tick again until
/// <paramref name="cooldown"/> has elapsed. While cooling, returns Failure (cheap to chain in
/// a Selector — "try something else").
/// </summary>
public sealed class Cooldown : IBehavior
{
    private readonly IBehavior _child;
    private readonly Func<BehaviorContext, TimeSpan> _cooldown;
    private TimeSpan _availableAt = TimeSpan.MinValue;

    public string Name { get; }
    public BehaviorStatus LastStatus { get; private set; } = BehaviorStatus.Failure;

    public Cooldown(string name, TimeSpan cooldown, IBehavior child)
        : this(name, _ => cooldown, child)
    {
    }

    public Cooldown(string name, Func<BehaviorContext, TimeSpan> cooldown, IBehavior child)
    {
        Name = name; _cooldown = cooldown; _child = child;
    }

    public BehaviorStatus Tick(BehaviorContext ctx)
    {
        if (BotMonotonicClock.Now < _availableAt) return LastStatus = BehaviorStatus.Failure;
        var s = _child.Tick(ctx);
        if (s != BehaviorStatus.Running) _availableAt = BotMonotonicClock.Now + _cooldown(ctx);
        return LastStatus = s;
    }

    public void Reset() { _child.Reset(); LastStatus = BehaviorStatus.Failure; }

    public void Visit(IBehaviorVisitor v)
    {
        v.Node(Name, LastStatus, 1);
        v.Down(); _child.Visit(v); v.Up();
    }
}

/// <summary>
/// Try child up to <paramref name="maxAttempts"/> times. After a Failure, optionally call
/// <paramref name="onRetry"/> (e.g. recompute a path, nudge cursor) before the next attempt.
/// Counter resets on Success. Used for the "loot 2-3 times → repath check → retry" pattern.
/// </summary>
public sealed class RetryWith : IBehavior
{
    private readonly IBehavior _child;
    private readonly int _maxAttempts;
    private readonly Action<BehaviorContext>? _onRetry;
    private int _attempt;

    public string Name { get; }
    public BehaviorStatus LastStatus { get; private set; } = BehaviorStatus.Failure;

    public RetryWith(string name, int maxAttempts, IBehavior child, Action<BehaviorContext>? onRetry = null)
    {
        Name = name; _maxAttempts = maxAttempts; _child = child; _onRetry = onRetry;
    }

    public BehaviorStatus Tick(BehaviorContext ctx)
    {
        var s = _child.Tick(ctx);
        if (s == BehaviorStatus.Success) { _attempt = 0; return LastStatus = BehaviorStatus.Success; }
        if (s == BehaviorStatus.Running) return LastStatus = BehaviorStatus.Running;

        // Failure path
        _attempt++;
        if (_attempt >= _maxAttempts) { _attempt = 0; return LastStatus = BehaviorStatus.Failure; }
        _onRetry?.Invoke(ctx);
        return LastStatus = BehaviorStatus.Running;
    }

    public void Reset() { _attempt = 0; _child.Reset(); LastStatus = BehaviorStatus.Failure; }

    public void Visit(IBehaviorVisitor v)
    {
        v.Node($"{Name} ({_attempt}/{_maxAttempts})", LastStatus, 1);
        v.Down(); _child.Visit(v); v.Up();
    }
}

/// <summary>
/// Wrap a child so it ticks Running until <paramref name="condition"/> becomes true (Success)
/// or <paramref name="timeout"/> elapses (Failure). Useful for "channel until enemy dies" or
/// "wait for panel to open".
/// </summary>
public sealed class Until : IBehavior
{
    private readonly IBehavior _child;
    private readonly Func<BehaviorContext, bool> _condition;
    private readonly TimeSpan _timeout;
    private TimeSpan _startedAt = TimeSpan.MinValue;

    public string Name { get; }
    public BehaviorStatus LastStatus { get; private set; } = BehaviorStatus.Failure;

    public Until(string name, Func<BehaviorContext, bool> condition, TimeSpan timeout, IBehavior child)
    {
        Name = name; _condition = condition; _timeout = timeout; _child = child;
    }

    public BehaviorStatus Tick(BehaviorContext ctx)
    {
        if (_startedAt == TimeSpan.MinValue) _startedAt = BotMonotonicClock.Now;
        if (_condition(ctx)) { Reset(); return LastStatus = BehaviorStatus.Success; }
        if (BotMonotonicClock.Now - _startedAt > _timeout) { Reset(); return LastStatus = BehaviorStatus.Failure; }
        _child.Tick(ctx);
        return LastStatus = BehaviorStatus.Running;
    }

    public void Reset() { _startedAt = TimeSpan.MinValue; _child.Reset(); LastStatus = BehaviorStatus.Failure; }

    public void Visit(IBehaviorVisitor v)
    {
        v.Node($"{Name} (timeout={_timeout.TotalSeconds:F1}s)", LastStatus, 1);
        v.Down(); _child.Visit(v); v.Up();
    }
}

/// <summary>Invert child status. Success ↔ Failure; Running passes through.</summary>
public sealed class Invert : IBehavior
{
    private readonly IBehavior _child;
    public string Name { get; }
    public BehaviorStatus LastStatus { get; private set; } = BehaviorStatus.Failure;

    public Invert(string name, IBehavior child) { Name = name; _child = child; }

    public BehaviorStatus Tick(BehaviorContext ctx) => LastStatus = _child.Tick(ctx) switch
    {
        BehaviorStatus.Success => BehaviorStatus.Failure,
        BehaviorStatus.Failure => BehaviorStatus.Success,
        _                      => BehaviorStatus.Running,
    };

    public void Reset() { _child.Reset(); LastStatus = BehaviorStatus.Failure; }

    public void Visit(IBehaviorVisitor v)
    {
        v.Node(Name, LastStatus, 1);
        v.Down(); _child.Visit(v); v.Up();
    }
}
