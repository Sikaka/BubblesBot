namespace BubblesBot.Bot.Behaviors;

/// <summary>
/// Flat per-node record of one Visit walk. The web UI reads a list of these to render the
/// tree as nested lines without needing the live behavior objects.
/// </summary>
public readonly record struct TreeNode(int Depth, string Name, BehaviorStatus Status);

/// <summary>Visitor that flattens a tree into a list of nodes with depth.</summary>
public sealed class TreeSnapshotVisitor : IBehaviorVisitor
{
    private readonly List<TreeNode> _nodes = new();
    private int _depth;
    public IReadOnlyList<TreeNode> Nodes => _nodes;

    public void Node(string name, BehaviorStatus status, int children)
        => _nodes.Add(new TreeNode(_depth, name, status));

    public void Down() => _depth++;
    public void Up()   => _depth--;

    public static IReadOnlyList<TreeNode> Capture(IBehavior root)
    {
        var v = new TreeSnapshotVisitor();
        root.Visit(v);
        return v.Nodes;
    }
}
