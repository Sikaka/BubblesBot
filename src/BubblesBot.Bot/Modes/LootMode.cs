using BubblesBot.Bot.Behaviors;
using BubblesBot.Bot.Behaviors.Loot;
using BubblesBot.Bot.Input;
using BubblesBot.Bot.Settings;
using BubblesBot.Bot.Systems;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Modes;

/// <summary>
/// Single-purpose mode: while the loot key is held, click visible in-range items. The actual
/// decision logic lives in the <see cref="LootClosestVisible"/> behavior; this mode just
/// owns the system instances and ticks the tree.
///
/// <para>The behavior-tree migration preserves the v1 looter behavior verbatim — same range
/// filter, visibility check, click-and-verify — but adds per-target attempt counting +
/// blacklist (3 failed clicks → ignore until area change) per the user's policy.</para>
/// </summary>
public sealed class LootMode : IBotMode
{
    private readonly Func<GameSnapshot?> _getCurrentSnapshot;
    private readonly SettingsStore _settings;
    private readonly InteractSystem _interact = new();
    private readonly LootClosestVisible _loot;
    private readonly IBehavior _root;

    public LootMode(Func<GameSnapshot?> getCurrentSnapshot, SettingsStore settings)
    {
        _getCurrentSnapshot = getCurrentSnapshot;
        _settings = settings;
        _loot = new LootClosestVisible("loot closest", _interact, getCurrentSnapshot)
        {
            // Manual hold-to-loot: the user pressing the key means "grab it" — ignore value filters.
            BypassValueFilter = true,
        };
        _root = _loot;
    }

    public string Name => "Loot";
    public string LastDecision => _loot.LastDecision;

    /// <summary>Stable address of whatever label the looter is currently targeting (for renderer highlight).</summary>
    public nint CurrentTargetLabelAddress => _loot.CurrentTargetAddress;

    public IBehavior Root => _root;

    /// <summary>Look up the current target in the supplied snapshot. Null when gone.</summary>
    public GroundLabelView? ResolveCurrentTarget(GameSnapshot? snapshot)
    {
        var addr = CurrentTargetLabelAddress;
        if (snapshot is null || addr == 0) return null;
        foreach (var l in snapshot.GroundLabels)
            if (l.LabelAddress == addr) return l;
        return null;
    }

    public void Reset() => _root.Reset();

    public void Tick(GameSnapshot snapshot, IInputRouter input)
    {
        var ctx = new BehaviorContext(snapshot, input, _settings.Current, live: null);
        _root.Tick(ctx);
    }
}
