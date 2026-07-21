using BubblesBot.Core.Game;
using BubblesBot.Core.Pathfinding;

namespace BubblesBot.Bot.Systems;

/// <summary>Minimal per-tick monster observation the boss tracker needs — decoupled from EntityCache for testability.</summary>
public readonly record struct BossObservation(uint Id, string Path, Vector2i Position, int HpCurrent, int HpMax);

/// <summary>
/// Positive map-boss death evidence. Configured with the expected boss metadata fragments (from
/// <see cref="Core.Knowledge.MapBossCatalog"/>); a boss is "dead" when observed at HP&lt;=0, or
/// (like <c>RitualPriorityTracker</c>'s inference) when a previously-alive match disappears during
/// a healthy scan while its last position sits inside the network bubble. Completion requires
/// every configured fragment to have at least one dead match — "one unique disappeared" is not
/// enough for multi-boss maps.
///
/// <para>Pure and unit-testable. Wiring it into the in-map controller to gate the boss-hunt phase
/// / <c>requireBossKill</c> completion is a separate live-validated step.</para>
/// </summary>
public sealed class BossEvidenceTracker
{
    private sealed class State { public Vector2i Position; public bool SeenAlive; public bool Dead; }

    private readonly Dictionary<uint, (string Fragment, State State)> _tracked = new();
    private string[] _fragments = [];

    public void Configure(IEnumerable<string> fragments)
    {
        _fragments = fragments.Where(f => !string.IsNullOrWhiteSpace(f)).ToArray();
        _tracked.Clear();
    }

    public bool HasExpectedBosses => _fragments.Length > 0;

    /// <summary>Every configured boss fragment has at least one match with death evidence.</summary>
    public bool IsComplete =>
        _fragments.Length > 0
        && _fragments.All(fragment => _tracked.Values.Any(t =>
            t.Fragment.Equals(fragment, StringComparison.OrdinalIgnoreCase) && t.State.Dead));

    public int BossesSeen => _tracked.Count;
    public int BossesDead => _tracked.Values.Count(t => t.State.Dead);

    public void Observe(IEnumerable<BossObservation> monsters, Vector2i player, bool scanHealthy)
    {
        if (_fragments.Length == 0) return;

        var present = new HashSet<uint>();
        foreach (var m in monsters)
        {
            var fragment = _fragments.FirstOrDefault(f => m.Path.Contains(f, StringComparison.OrdinalIgnoreCase));
            if (fragment is null) continue;

            present.Add(m.Id);
            if (!_tracked.TryGetValue(m.Id, out var entry))
                _tracked[m.Id] = entry = (fragment, new State());
            entry.State.Position = m.Position;
            if (m.HpCurrent > 0) entry.State.SeenAlive = true;
            if (m.HpMax > 0 && m.HpCurrent <= 0) entry.State.Dead = true;
        }

        // Inferred death: a previously-alive boss that vanished during a healthy scan while its
        // last position was inside the network bubble. Never infer from a degraded scan.
        if (!scanHealthy) return;
        var bubble = GridConstants.NetworkBubbleGrid;
        var bubble2 = (long)bubble * bubble;
        foreach (var (id, entry) in _tracked)
        {
            if (entry.State.Dead || !entry.State.SeenAlive || present.Contains(id)) continue;
            long dx = entry.State.Position.X - player.X;
            long dy = entry.State.Position.Y - player.Y;
            if (dx * dx + dy * dy <= bubble2) entry.State.Dead = true;
        }
    }

    public void Reset()
    {
        _tracked.Clear();
        _fragments = [];
    }
}
