using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.LiveTests;

/// <summary>
/// Read-only research capture for The Formed. The operator owns every interaction while this
/// test records area transitions, the arena activator, unique-boss life/state, deaths, drops,
/// and the eventual return to the originating hideout.
/// </summary>
public sealed class GuardianFormedEncounterMonitorLiveTest : ILiveTestCase
{
    private const int PollMilliseconds = 100;
    private static readonly string[] InterestingPathTerms =
    [
        "Maven", "Crucible", "Invitation", "Initiator", "BossArena", "MapDevice",
        "Portal", "AreaTransition", "ShaperGuardian", "AtlasExile",
    ];

    private static readonly string[] InterestingUiTerms =
    [
        "Maven", "Crucible", "Invitation", "Resurrect", "Return", "Portal",
        "Phoenix", "Minotaur", "Chimera", "Hydra",
    ];

    public string Id => "G-08-formed-encounter-monitor";
    public string Name => "The Formed encounter monitor";
    public string Description => "Read-only, change-only capture from staged invitation through arena completion and return.";
    public string ManualSetup => "In hideout with Invitation: The Formed staged in the visible Map Receptacle. Start the test, then manually activate, enter, start, fight, loot, and leave.";
    public LiveTestMutation Mutation => LiveTestMutation.ReadOnly;
    public bool DrivesInput => false;
    public IReadOnlySet<string> AllowedBlockingPanels => OpenPanelsView.BlockingPanels;

    public async Task<LiveTestCaseResult> RunAsync(
        LiveTestContext context,
        CancellationToken cancellationToken)
    {
        var initial = context.Snapshot();
        var startingArea = initial.AreaHash;
        var staged = initial.MapReceptacle.Item();
        context.Check(startingArea != 0, "starting area", $"0x{startingArea:X8}");
        if (initial.MapReceptacle.IsVisible)
        {
            context.Check(staged is { } && staged.Value.BaseName.Contains("The Formed", StringComparison.OrdinalIgnoreCase),
                "staged Formed invitation",
                staged is { } item ? $"base='{item.BaseName}' rarity={item.Rarity}" : "missing");
        }
        else
        {
            // Starting before the Kirac selection is a supported capture point. The operator
            // can open/stage/activate while the read-only monitor waits for the portal load.
            context.Observe("pre-Kirac start", "Map Receptacle not visible yet; waiting for manual invitation selection");
        }

        context.Observe("monitor armed",
            $"read-only; startingArea=0x{startingArea:X8}; waiting for manual activation and portal entry");

        var entities = new EntityCache(initial.Reader);
        var bossStates = new Dictionary<uint, BossState>();
        var primaryGuardians = new Dictionary<string, uint>(StringComparer.Ordinal);
        var seenInterestingEntities = new HashSet<string>(StringComparer.Ordinal);
        var seenDrops = new HashSet<string>(StringComparer.Ordinal);
        var lastUiLines = new HashSet<string>(StringComparer.Ordinal);
        var lastGameState = context.GameState;
        var lastArea = startingArea;
        uint? encounterArea = null;
        var encounterEntries = 0;
        var deathCount = 0;
        var deathVisible = false;
        var completionLikely = false;
        var encounterWasEntered = false;
        var returnedAfterCompletionAt = DateTime.MinValue;
        var nextUiScanAt = DateTime.MinValue;
        var nextHealthSummaryAt = DateTime.MinValue;
        long? lastPodiumActivation = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var gameState = context.GameState;
            if (gameState != lastGameState)
            {
                context.Observe("game state", $"{lastGameState} -> {gameState}");
                lastGameState = gameState;
            }

            if (gameState != GameStateKind.InGame)
            {
                await Task.Delay(PollMilliseconds, cancellationToken);
                continue;
            }

            var snapshot = context.Snapshot();
            var player = snapshot.Player;
            var area = snapshot.AreaHash;
            if (area == 0 || player is null)
            {
                await Task.Delay(PollMilliseconds, cancellationToken);
                continue;
            }

            if (area != lastArea)
            {
                var previous = lastArea;
                lastArea = area;
                entities.Clear();
                context.Observe("area transition",
                    $"0x{previous:X8} -> 0x{area:X8} grid=({player.GridPosition.X},{player.GridPosition.Y})");

                if (area != startingArea && encounterArea is null)
                {
                    encounterArea = area;
                    encounterWasEntered = true;
                    encounterEntries++;
                    context.Observe("encounter entered", $"area=0x{area:X8} entry={encounterEntries}");
                }
                else if (encounterArea == area)
                {
                    encounterWasEntered = true;
                    encounterEntries++;
                    context.Observe("encounter re-entered", $"area=0x{area:X8} entry={encounterEntries}");
                }
                else if (encounterArea is not null)
                {
                    context.Observe("encounter exited",
                        $"destination=0x{area:X8} completionLikely={completionLikely} deaths={deathCount}");
                    if (completionLikely)
                        returnedAfterCompletionAt = DateTime.UtcNow;
                }

                nextUiScanAt = DateTime.MinValue;
            }

            RefreshEntities(snapshot, entities);
            ObserveInterestingEntities(context, entities, seenInterestingEntities);

            var inEncounter = encounterArea == area;
            if (inEncounter)
            {
                ObservePodiumState(context, snapshot, entities, ref lastPodiumActivation);
                ObserveBosses(context, entities, bossStates, primaryGuardians);
                ObserveDrops(context, snapshot, seenDrops);

                var primaryAlive = primaryGuardians.Values.Count(id =>
                    !bossStates.TryGetValue(id, out var state) || state.Alive);
                var allPrimaryGuardiansDown = primaryGuardians.Count == 4 && primaryAlive == 0;
                var hasMavenLootEvidence = seenDrops.Any(x =>
                    x.Contains("CurrencyMavenKeyFragment", StringComparison.Ordinal));
                if (!completionLikely && allPrimaryGuardiansDown && hasMavenLootEvidence)
                {
                    completionLikely = true;
                    context.Observe("completion evidence",
                        $"primaryGuardians={primaryGuardians.Count} alive={primaryAlive} " +
                        $"mavenFragmentDrop={hasMavenLootEvidence} drops={seenDrops.Count}");
                }

                if (DateTime.UtcNow >= nextHealthSummaryAt)
                {
                    context.Observe("encounter heartbeat",
                        $"primaryGuardians={primaryGuardians.Count} alive={primaryAlive} " +
                        $"trackedUniques={bossStates.Count} drops={seenDrops.Count} " +
                        $"playerHp={player.Life.Current}/{player.Life.Max} grid=({player.GridPosition.X},{player.GridPosition.Y})");
                    nextHealthSummaryAt = DateTime.UtcNow.AddSeconds(5);
                }
            }

            var resurrectVisible = snapshot.ResurrectPanel.IsVisible;
            if (resurrectVisible != deathVisible)
            {
                deathVisible = resurrectVisible;
                if (resurrectVisible) deathCount++;
                context.Observe(resurrectVisible ? "player death" : "death panel cleared",
                    $"deaths={deathCount} area=0x{area:X8} hp={player.Life.Current}/{player.Life.Max}");
            }

            if (DateTime.UtcNow >= nextUiScanAt)
            {
                ObserveRelevantUi(context, snapshot, lastUiLines);
                nextUiScanAt = DateTime.UtcNow.AddSeconds(1);
            }

            if (returnedAfterCompletionAt != DateTime.MinValue
                && DateTime.UtcNow - returnedAfterCompletionAt >= TimeSpan.FromSeconds(2))
            {
                context.Check(true, "capture lifecycle",
                    $"entered={encounterWasEntered} entries={encounterEntries} primaryGuardians={primaryGuardians.Count} " +
                    $"drops={seenDrops.Count} deaths={deathCount} returnedArea=0x{area:X8}");
                return LiveTestCaseResult.Pass(
                    $"captured The Formed through completion and return; guardians={primaryGuardians.Count}, drops={seenDrops.Count}, deaths={deathCount}",
                    "ReadOnlyEncounterCapture");
            }

            await Task.Delay(PollMilliseconds, cancellationToken);
        }
    }

    private static void RefreshEntities(GameSnapshot snapshot, EntityCache entities)
    {
        if (snapshot.Reader.TryReadStruct<nint>(
                snapshot.IngameDataAddress + KnownOffsets.IngameData.EntityList, out var list)
            && list != 0)
            entities.Refresh(list, snapshot.Player?.GridPosition ?? default);
    }

    private static void ObserveInterestingEntities(
        LiveTestContext context,
        EntityCache entities,
        HashSet<string> seen)
    {
        foreach (var entity in entities.Entries.Values)
        {
            if (!InterestingPathTerms.Any(term =>
                    entity.Path.Contains(term, StringComparison.OrdinalIgnoreCase)))
                continue;

            var key = $"{entity.Id}:{entity.Path}";
            if (!seen.Add(key)) continue;
            context.Observe("interesting entity",
                $"id={entity.Id} kind={entity.Kind} rarity={entity.Rarity} name='{entity.Name}' " +
                $"targetable={entity.IsTargetable} hp={entity.HpCurrent}/{entity.HpMax} " +
                $"grid=({entity.GridPosition.X},{entity.GridPosition.Y}) path='{entity.Path}'");
        }
    }

    private static void ObserveBosses(
        LiveTestContext context,
        EntityCache entities,
        Dictionary<uint, BossState> states,
        Dictionary<string, uint> primaryGuardians)
    {
        foreach (var entity in entities.Entries.Values)
        {
            if (entity.Kind != EntityListReader.EntityKind.Monster
                || entity.Rarity != EntityListReader.EntityRarity.Unique
                || !entity.HasLife)
                continue;

            var guardianFamily = GuardianFamily(entity.Path);
            var isMaven = entity.Path.Contains(
                "TheMavenProvingGrounds", StringComparison.OrdinalIgnoreCase);
            if (guardianFamily is null && !isMaven)
                continue; // Maven's volatile orbs are Unique monsters, but not encounter bosses.

            if (guardianFamily is not null
                && primaryGuardians.TryAdd(guardianFamily, entity.Id))
                context.Observe("primary guardian bound",
                    $"family={guardianFamily} id={entity.Id} path='{entity.Path}'");

            var hpPercent = entity.HpMax > 0
                ? Math.Clamp((int)Math.Ceiling(entity.HpCurrent * 100d / entity.HpMax), 0, 100)
                : 0;
            var bucket = hpPercent == 0 ? 0 : (hpPercent + 9) / 10 * 10;
            var current = new BossState(
                entity.Name,
                entity.Path,
                entity.HpCurrent > 0,
                bucket,
                entity.IsTargetable,
                entity.IsDormant,
                entity.IsStale);

            if (states.TryGetValue(entity.Id, out var previous) && previous == current)
                continue;

            states[entity.Id] = current;
            context.Observe(previous is null ? "unique boss discovered" : "unique boss state",
                $"id={entity.Id} name='{entity.Name}' hp={entity.HpCurrent}/{entity.HpMax} " +
                $"bucket={bucket}% targetable={entity.IsTargetable} dormant={entity.IsDormant} " +
                $"stale={entity.IsStale} path='{entity.Path}'");
        }
    }

    private static void ObservePodiumState(
        LiveTestContext context,
        GameSnapshot snapshot,
        EntityCache entities,
        ref long? previousActivation)
    {
        var podium = entities.Entries.Values.FirstOrDefault(entity =>
            entity.Path.StartsWith(
                "Metadata/Terrain/EndGame/MapAtlasMaven/Objects/MavenBossRushObject",
                StringComparison.Ordinal));
        if (podium?.StateMachineCompAddr is not { } stateMachine || stateMachine == 0)
            return;

        var activation = StateMachineView.ReadValue(
            snapshot.Reader, stateMachine, MavenInvitationStates.BossRushObject.CurrentPhase);
        if (activation is null || activation == previousActivation)
            return;

        previousActivation = activation;
        context.Observe("Formed podium activation",
            $"raw={activation} phase={MavenInvitationStates.BossRushObject.Describe(activation.Value)} " +
            $"targetable={podium.IsTargetable} grid=({podium.GridPosition.X},{podium.GridPosition.Y})");
    }

    private static string? GuardianFamily(string path)
    {
        if (!path.Contains("/AtlasBosses/", StringComparison.OrdinalIgnoreCase)) return null;
        if (path.Contains("HydraBossStandalone", StringComparison.OrdinalIgnoreCase)) return "Hydra";
        if (path.Contains("MinotaurBossStandalone", StringComparison.OrdinalIgnoreCase)) return "Minotaur";
        if (path.Contains("ChimeraBossStandalone", StringComparison.OrdinalIgnoreCase)) return "Chimera";
        if (path.Contains("PhoenixBossStandalone", StringComparison.OrdinalIgnoreCase)) return "Phoenix";
        return null;
    }

    private static void ObserveDrops(
        LiveTestContext context,
        GameSnapshot snapshot,
        HashSet<string> seen)
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

    private static void ObserveRelevantUi(
        LiveTestContext context,
        GameSnapshot snapshot,
        HashSet<string> previous)
    {
        var view = VisibleUiTextView.ReadInGame(snapshot.Reader, snapshot.IngameStateAddress, 8_000, 24);
        var current = view.Elements
            .Select(x => OneLine(x.Text))
            .Where(x => InterestingUiTerms.Any(term => x.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var line in current.Except(previous).Order(StringComparer.Ordinal))
            context.Observe("relevant UI appeared", $"'{line}'");
        foreach (var line in previous.Except(current).Order(StringComparer.Ordinal))
            context.Observe("relevant UI cleared", $"'{line}'");

        previous.Clear();
        previous.UnionWith(current);
    }

    private static string OneLine(string text)
        => text.Replace('\r', ' ').Replace('\n', '|').Trim();

    private sealed record BossState(
        string Name,
        string Path,
        bool Alive,
        int HpBucket,
        bool Targetable,
        bool Dormant,
        bool Stale);
}
