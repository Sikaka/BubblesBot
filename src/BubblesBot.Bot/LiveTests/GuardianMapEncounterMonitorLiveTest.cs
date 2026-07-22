using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>Read-only manual-play capture for one Shaper Guardian map.</summary>
public sealed class GuardianMapEncounterMonitorLiveTest : ILiveTestCase
{
    public string Id => "G-09-guardian-map-monitor";
    public string Name => "Shaper Guardian map monitor";
    public string Description => "Tracks a manually played guardian map through boss death, drops, and exit without sending input.";
    public string ManualSetup => "Begin in hideout before entering, or anywhere inside an active Shaper Guardian map, then play and leave normally.";
    public LiveTestMutation Mutation => LiveTestMutation.ReadOnly;
    public bool DrivesInput => false;
    public IReadOnlySet<string> AllowedBlockingPanels => OpenPanelsView.BlockingPanels;

    public async Task<LiveTestCaseResult> RunAsync(LiveTestContext context, CancellationToken cancellationToken)
    {
        var first = context.Snapshot();
        var cache = new EntityCache(first.Reader);
        Refresh(first, cache);
        var initialArea = first.AreaHash;
        var initialLooksLikeHideout = cache.Entries.Values.Any(x =>
            x.Kind == EntityListReader.EntityKind.Stash
            || x.Path.Contains("HideoutStash", StringComparison.OrdinalIgnoreCase));
        uint? mapArea = initialLooksLikeHideout ? null : initialArea;
        var lastArea = initialArea;
        var lastState = context.GameState;
        var bossStates = new Dictionary<uint, BossState>();
        uint? primaryBoss = null;
        var drops = new HashSet<string>(StringComparer.Ordinal);
        var deathVisible = false;
        var deaths = 0;
        var nextHeartbeat = DateTime.MinValue;
        var exitObservedAt = DateTime.MinValue;

        context.Check(initialArea != 0, "starting area", $"0x{initialArea:X8}");
        context.Observe("guardian monitor armed",
            $"initialArea=0x{initialArea:X8} initialLooksLikeHideout={initialLooksLikeHideout} mapArea={Format(mapArea)}");

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = context.GameState;
            if (state != lastState)
            {
                context.Observe("game state", $"{lastState} -> {state}");
                lastState = state;
            }
            if (state != GameStateKind.InGame)
            {
                await Task.Delay(100, cancellationToken);
                continue;
            }

            var snapshot = context.Snapshot();
            if (snapshot.Player is not { } player || snapshot.AreaHash == 0)
            {
                await Task.Delay(100, cancellationToken);
                continue;
            }

            var area = snapshot.AreaHash;
            if (area != lastArea)
            {
                var previous = lastArea;
                lastArea = area;
                cache.Clear();
                context.Observe("area transition", $"0x{previous:X8} -> 0x{area:X8}");
                if (mapArea is null)
                {
                    mapArea = area;
                    context.Observe("guardian map entered", $"area=0x{area:X8}");
                }
                else if (area != mapArea && BossIsDown(primaryBoss, bossStates))
                {
                    exitObservedAt = DateTime.UtcNow;
                    context.Observe("guardian map exited",
                        $"mapArea={Format(mapArea)} destination=0x{area:X8} bossDown=True deaths={deaths}");
                }
            }

            Refresh(snapshot, cache);
            ObserveGuardianBosses(context, cache, bossStates, ref primaryBoss);
            ObserveDrops(context, snapshot, drops);

            var resurrect = snapshot.ResurrectPanel.IsVisible;
            if (resurrect != deathVisible)
            {
                deathVisible = resurrect;
                if (resurrect) deaths++;
                context.Observe(resurrect ? "player death" : "death panel cleared",
                    $"deaths={deaths} hp={player.Life.Current}/{player.Life.Max} area=0x{area:X8}");
            }

            if (DateTime.UtcNow >= nextHeartbeat)
            {
                var boss = primaryBoss is { } id && bossStates.TryGetValue(id, out var b) ? b : null;
                context.Observe("guardian heartbeat",
                    $"area=0x{area:X8} boss='{boss?.Name ?? "not-yet-seen"}' " +
                    $"bossAlive={boss?.Alive.ToString() ?? "unknown"} hpBucket={boss?.HpBucket.ToString() ?? "?"}% " +
                    $"drops={drops.Count} playerHp={player.Life.Current}/{player.Life.Max}");
                nextHeartbeat = DateTime.UtcNow.AddSeconds(5);
            }

            if (exitObservedAt != DateTime.MinValue
                && DateTime.UtcNow - exitObservedAt >= TimeSpan.FromSeconds(2))
            {
                context.Check(true, "guardian lifecycle",
                    $"mapArea={Format(mapArea)} boss={primaryBoss} bossDown=True drops={drops.Count} deaths={deaths} exitArea=0x{area:X8}");
                return LiveTestCaseResult.Pass(
                    $"captured guardian map through boss death and exit; drops={drops.Count}, deaths={deaths}",
                    "ReadOnlyGuardianCapture");
            }

            await Task.Delay(100, cancellationToken);
        }
    }

    private static void Refresh(GameSnapshot snapshot, EntityCache cache)
    {
        if (snapshot.Reader.TryReadStruct<nint>(
                snapshot.IngameDataAddress + KnownOffsets.IngameData.EntityList, out var list)
            && list != 0)
            cache.Refresh(list, snapshot.Player?.GridPosition ?? default);
    }

    private static void ObserveGuardianBosses(
        LiveTestContext context,
        EntityCache cache,
        Dictionary<uint, BossState> states,
        ref uint? primaryBoss)
    {
        foreach (var entity in cache.Entries.Values)
        {
            if (entity.Kind != EntityListReader.EntityKind.Monster
                || entity.Rarity != EntityListReader.EntityRarity.Unique
                || !entity.HasLife
                || !IsGuardian(entity.Path, entity.Name))
                continue;

            if (primaryBoss is null)
            {
                primaryBoss = entity.Id;
                context.Observe("primary guardian bound",
                    $"id={entity.Id} name='{entity.Name}' path='{entity.Path}'");
            }

            var percent = entity.HpMax > 0
                ? Math.Clamp((int)Math.Ceiling(entity.HpCurrent * 100d / entity.HpMax), 0, 100)
                : 0;
            var bucket = percent == 0 ? 0 : (percent + 9) / 10 * 10;
            var current = new BossState(entity.Name, entity.Path, entity.HpCurrent > 0,
                bucket, entity.IsTargetable, entity.IsDormant, entity.IsStale);
            if (states.TryGetValue(entity.Id, out var prior) && prior == current) continue;
            states[entity.Id] = current;
            context.Observe(prior is null ? "guardian discovered" : "guardian state",
                $"id={entity.Id} name='{entity.Name}' hp={entity.HpCurrent}/{entity.HpMax} bucket={bucket}% " +
                $"targetable={entity.IsTargetable} dormant={entity.IsDormant} stale={entity.IsStale} path='{entity.Path}'");
        }
    }

    private static void ObserveDrops(LiveTestContext context, GameSnapshot snapshot, HashSet<string> seen)
    {
        foreach (var label in snapshot.GroundLabels)
        {
            if (!label.IsItem) continue;
            var key = $"{label.EntityId}:{label.InnerItemPath}:{label.ItemName}";
            if (!seen.Add(key)) continue;
            context.Observe("drop appeared",
                $"name='{label.ItemName}' base='{label.BaseName}' stack={label.StackCount} " +
                $"visible={label.IsLabelVisible} path='{label.InnerItemPath}'");
        }
    }

    private static bool IsGuardian(string path, string name)
        => path.Contains("/AtlasBosses/", StringComparison.OrdinalIgnoreCase)
           && (path.Contains("Phoenix", StringComparison.OrdinalIgnoreCase)
               || path.Contains("Minotaur", StringComparison.OrdinalIgnoreCase)
               || path.Contains("Chimera", StringComparison.OrdinalIgnoreCase)
               || path.Contains("Hydra", StringComparison.OrdinalIgnoreCase))
           || name.StartsWith("Guardian of the ", StringComparison.OrdinalIgnoreCase);

    private static bool BossIsDown(uint? id, IReadOnlyDictionary<uint, BossState> states)
        => id is { } value && states.TryGetValue(value, out var state) && !state.Alive;

    private static string Format(uint? area) => area is { } value ? $"0x{value:X8}" : "unknown";

    private sealed record BossState(
        string Name,
        string Path,
        bool Alive,
        int HpBucket,
        bool Targetable,
        bool Dormant,
        bool Stale);
}
