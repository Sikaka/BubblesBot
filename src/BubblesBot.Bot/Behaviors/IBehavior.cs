using BubblesBot.Bot.Input;
using BubblesBot.Bot.Settings;
using BubblesBot.Bot.Strategies;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Behaviors;

/// <summary>
/// Result of one tick of a behavior. Standard behavior-tree triad.
/// <list type="bullet">
///   <item><c>Running</c> — still working; tick again next frame.</item>
///   <item><c>Success</c> — goal achieved this tick.</item>
///   <item><c>Failure</c> — couldn't achieve goal (out of range, blocked, gate refused, etc).</item>
/// </list>
/// </summary>
public enum BehaviorStatus { Running, Success, Failure }

/// <summary>
/// Per-tick state every behavior needs. Lean on purpose — anything a behavior wants beyond
/// this gets passed in via constructor (e.g. a system reference).
/// </summary>
public sealed class BehaviorContext
{
    public BehaviorContext(GameSnapshot snapshot, IInputRouter input, BotSettings settings, LivePlayer? live,
        EntityCache? entities = null, FarmingStrategy? strategy = null)
    {
        Snapshot = snapshot;
        Input    = input;
        Settings = settings;
        Live     = live;
        Entities = entities;
        Strategy = strategy;
    }

    public GameSnapshot   Snapshot { get; }
    public IInputRouter   Input    { get; }
    public BotSettings    Settings { get; }
    public LivePlayer?    Live     { get; }
    public EntityCache?   Entities { get; }

    /// <summary>
    /// The active farming strategy, when the map-farming lifecycle built this context. Null for
    /// other modes (Overlay/Blight/Simulacrum) and any context created outside mode 4.
    /// </summary>
    public FarmingStrategy? Strategy { get; }
}

/// <summary>
/// One tickable unit of behavior. Implementations are expected to be re-entrant and idempotent
/// per tick — every tick they re-evaluate from current world state. Cross-tick state is fine
/// (a counter, a target lock) but the behavior must tolerate ticks where the world has shifted
/// underneath it.
///
/// <para>Names are surfaced to the web UI for tree visualization, so make them short and
/// describe what the behavior does, not how — "loot closest" not "click visible label".</para>
/// </summary>
public interface IBehavior
{
    string Name { get; }

    BehaviorStatus Tick(BehaviorContext ctx);

    /// <summary>
    /// Called when the behavior is interrupted before reaching Success/Failure (parent
    /// short-circuited, mode swap, area change). Default is no-op; override to release
    /// held inputs, drop pending state, etc.
    /// </summary>
    void Reset() { }

    /// <summary>
    /// Walk this node and its children for diagnostics / web UI rendering. Default emits self;
    /// composers override to recurse.
    /// </summary>
    void Visit(IBehaviorVisitor v) => v.Node(Name, LastStatus, children: 0);

    /// <summary>Last status this behavior returned. Composers update this in their own Tick.</summary>
    BehaviorStatus LastStatus { get; }
}

/// <summary>Tree-walk callback for the web UI / debug overlay.</summary>
public interface IBehaviorVisitor
{
    void Node(string name, BehaviorStatus status, int children);
    void Down();
    void Up();
}
