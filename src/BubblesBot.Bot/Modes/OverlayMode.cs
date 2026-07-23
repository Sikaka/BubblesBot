using BubblesBot.Bot.Behaviors;
using BubblesBot.Bot.Behaviors.Interact;
using BubblesBot.Bot.Input;
using BubblesBot.Bot.Settings;
using BubblesBot.Bot.Systems;
using BubblesBot.Core.Pathfinding;
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
    private readonly InteractSystem _interact = new();
    private readonly MovementSystem _interactionMovement;
    private readonly SkillBook _interactionSkills = new();
    private readonly InteractWorldEntity _takeNearbyShrine;
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
        _interactionMovement = new MovementSystem(settings);
        _takeNearbyShrine = new InteractWorldEntity(
            "manual interact: shrine",
            _interact,
            _interactionMovement,
            _interactionSkills,
            NearbyAvailableShrine,
            (ctx, entry) => ctx.Entities is null
                || !ctx.Entities.Entries.TryGetValue(entry.Id, out var current)
                || current.ShrineAvailable is { IsKnown: true, IsTrue: false },
            allowGapCrossing: false,
            retryUntilActivated: true);
    }

    public string Name => "Overlay / manual";
    public IBehavior Root => _loot.Root;
    public string LastDecision { get; private set; } = "manual assistance ready";

    public void Tick(GameSnapshot snapshot, IInputRouter input)
    {
        var ctx = new BehaviorContext(snapshot, input, _settings.Current, _getLive(), _getEntities());
        var flaskFired = _flasks.Tick(ctx);
        if (_shouldLoot())
        {
            if (NearbyAvailableShrine(ctx) is not null)
            {
                _takeNearbyShrine.Tick(ctx);
                LastDecision = $"interact: {_takeNearbyShrine.LastDecision}"
                    + (flaskFired ? "; flask fired" : "");
                return;
            }

            _loot.Tick(snapshot, input);
            LastDecision = $"interact: {_loot.LastDecision}"
                + (flaskFired ? "; flask fired" : "");
            return;
        }

        LastDecision = flaskFired ? "manual play; flask fired" : "manual play; monitoring";
    }

    public void Reset()
    {
        _loot.Reset();
        _takeNearbyShrine.Reset();
        _interactionMovement.Release();
        _flasks.Reset();
    }

    private static EntityCache.Entry? NearbyAvailableShrine(BehaviorContext ctx)
    {
        if (ctx.Live is not { } live || ctx.Entities is null) return null;
        var maxDistance = ctx.Settings.InteractionRangeGrid;
        var maxDistance2 = maxDistance * maxDistance;
        EntityCache.Entry? best = null;
        var bestDistance2 = float.MaxValue;
        foreach (var mechanic in new MechanicsView(ctx.Entities).Entries)
        {
            if (!IsSafeManualInteraction(mechanic)) continue;
            var dx = mechanic.GridPosition.X - live.GridPosition.X;
            var dy = mechanic.GridPosition.Y - live.GridPosition.Y;
            var distance2 = (float)dx * dx + (float)dy * dy;
            if (distance2 > maxDistance2 || distance2 >= bestDistance2) continue;

            var targeting = ctx.Snapshot.Nav.TargetingReader;
            if (targeting is not null
                && !PathSmoother.HasLineOfSight(
                    targeting,
                    live.GridPosition.X,
                    live.GridPosition.Y,
                    mechanic.GridPosition.X,
                    mechanic.GridPosition.Y,
                    minValue: 1))
                continue;

            best = mechanic.Entry;
            bestDistance2 = distance2;
        }
        return best;
    }

    internal static bool IsSafeManualInteraction(MechanicEntry mechanic)
        => mechanic.Kind == MechanicKind.Shrine && mechanic.IsAvailable;
}
