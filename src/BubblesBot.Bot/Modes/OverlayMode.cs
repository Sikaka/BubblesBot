using BubblesBot.Bot.Behaviors;
using BubblesBot.Bot.Input;
using BubblesBot.Bot.Settings;
using BubblesBot.Bot.Systems;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Modes;

/// <summary>
/// Manual-play assistance mode. The renderer publishes monsters, mechanics, routes, loot values,
/// and key tiles; this controller owns the only input assists: configured flasks and hold-to-loot.
/// It never moves or attacks for the player.
/// </summary>
public sealed class OverlayMode : IBotMode
{
    private readonly SettingsStore _settings;
    private readonly LootMode _loot;
    private readonly FlaskSystem _flasks = new();
    private readonly Func<LivePlayer?> _getLive;
    private readonly Func<EntityCache?> _getEntities;
    private readonly Func<bool> _shouldLoot;

    public OverlayMode(SettingsStore settings, LootMode loot,
        Func<LivePlayer?> getLive, Func<EntityCache?> getEntities, Func<bool> shouldLoot)
    {
        _settings = settings;
        _loot = loot;
        _getLive = getLive;
        _getEntities = getEntities;
        _shouldLoot = shouldLoot;
    }

    public string Name => "Overlay / manual";
    public IBehavior Root => _loot.Root;
    public string LastDecision { get; private set; } = "manual assistance ready";

    public void Tick(GameSnapshot snapshot, IInputRouter input)
    {
        var ctx = new BehaviorContext(snapshot, input, _settings.Current, _getLive(), _getEntities());
        var flaskFired = _flasks.Tick(ctx);
        if (_shouldLoot()) _loot.Tick(snapshot, input);
        LastDecision = _shouldLoot()
            ? $"loot: {_loot.LastDecision}{(flaskFired ? "; flask fired" : "")}" 
            : flaskFired ? "manual play; flask fired" : "manual play; monitoring";
    }

    public void Reset()
    {
        _loot.Reset();
        _flasks.Reset();
    }
}
