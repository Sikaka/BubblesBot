using BubblesBot.Bot.Systems;
using BubblesBot.Core.Game;
using BubblesBot.Core.Pathfinding;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Behaviors.Loot;

/// <summary>
/// Picks the closest in-range visible ground label and clicks it via <see cref="InteractSystem"/>.
/// "Loot" key is overloaded as a generic "interact with whatever is nearest" key — it now picks
/// up items, opens chests, and clicks closed doors. Closest label wins regardless of category,
/// so the bot empties what's under its feet before pathing to anything further.
///
/// <para>Categorization is path-based:
/// <list type="bullet">
///   <item><b>Item</b> — outer path is <c>Metadata/MiscellaneousObjects/WorldItem</c> wrapping
///         a <c>Metadata/Items/...</c> entity. Subject to the price filter.</item>
///   <item><b>Chest</b> — outer path under <c>Metadata/Chests/</c>. Includes strongboxes,
///         barrels, and Blight chests. Label visibility implies "still interactable" — PoE
///         drops the label once a chest is opened, so no explicit IsOpened check is needed.</item>
///   <item><b>Door</b> — outer path contains <c>/Door</c>. Covers labyrinth/quest doors and
///         door-style blockages that surface a ground label. Zone-transition portals are NOT
///         in this category (they don't carry "Door" in their path) — those are handled by
///         <c>EnterAreaTransition</c> so the loot key can't accidentally zone you out.</item>
/// </list></para>
///
/// <para>Per-target attempt tracking + blacklist:
/// <list type="bullet">
///   <item>Same target tried <see cref="MaxAttemptsBeforeBlacklist"/> times → blacklisted for the area.</item>
///   <item>Each new target resets attempts.</item>
///   <item>Blacklist clears on <see cref="Reset"/> (called on area change by the owning mode).</item>
/// </list></para>
/// </summary>
public sealed class LootClosestVisible : IBehavior
{
    /// <summary>Maximum distance this behavior will CLICK a label from. Callers that gate on
    /// the (larger) LootRangeGrid must walk the player inside this before expecting a pickup
    /// — see PushCombatMode's loot-approach branch.</summary>
    public const float ClickRangeGrid = 25f;

    private const int MaxAttemptsBeforeBlacklist = 3;
    // Lockstep pickup confirmation can exceed 500 ms under combat load. The old timeout
    // deferred an Ancient Orb at distance 2 into end-of-map memory after one visible click.
    private const int ClickTimeoutMs = 1500;
    private const int MinItemClickIntervalMs = 300;

    /// <summary>How long a label must stay fully covered by a persistent structure label
    /// before its grid POSITION is written off for the area. Kept under the sweep's own
    /// strike-out window so the durable position give-up wins the race.</summary>
    private const double PersistentCoverGiveUpSeconds = 2.5;
    /// <summary>Radius of the position give-up — a covered label's address re-mints as it
    /// scrolls off-screen and back, but its drop position is stable to within a cell or two.</summary>
    private const float AbandonRadiusGrid = 6f;

    private readonly InteractSystem _interact;
    private readonly Func<GameSnapshot?> _liveSnapshot;
    private readonly HashSet<nint> _blacklist = new();
    // Positions written off because a persistent structure label (essence monolith, strongbox,
    // door, on-ground Ritual Rewards button) sits fully on top of the drop and will not clear
    // by looting. Position-keyed, not address-keyed: the label re-registers under a fresh
    // address every time it scrolls off-screen and back, so an address blacklist never sticks
    // (live 2026-07-16: a Glassblower's Bauble under a 530x212 essence-Monolith label).
    private readonly List<Vector2i> _abandonedSpots = new();
    private Vector2i? _coverTimerPos;
    private TimeSpan _coverTimerSince = TimeSpan.MinValue;
    private nint _lastTargetAddress;
    private int _attemptsOnTarget;
    private PendingPickup? _pendingPickup;
    private TimeSpan _lastItemClickAt = TimeSpan.MinValue;

    private sealed record PendingPickup(
        InteractTicket Ticket,
        string Name,
        int StackCount,
        int OccupiedCells,
        LootEvaluation Evaluation);

    public readonly record struct ConfirmedPickup(
        string Name, int StackCount, int OccupiedCells, LootEvaluation Evaluation);

    /// <summary>
    /// Fired exactly once after the clicked ground label positively disappears. Consumers can
    /// maintain conservative inventory occupancy without reopening the inventory every wave.
    /// </summary>
    public event Action<ConfirmedPickup>? PickupConfirmed;

    /// <summary>When true, the value/chest filters are ignored — every visible in-range item and
    /// chest is a candidate. Set for manual hold-to-loot (the user pressing the key means "grab it");
    /// left false for automated modes that should respect the configured filters.</summary>
    public bool BypassValueFilter { get; set; }
    /// <summary>Skip the targeting-terrain segment gate for a mechanic-owned reward pile.
    /// The caller must already have proven the pile is at its interaction anchor.</summary>
    public bool BypassTerrainLineOfSight { get; set; }
    /// <summary>Ignore the completed Ultimatum altar's ground-label rectangle when choosing an
    /// item-label click point. The altar label remains visible after rewards drop, but it is no
    /// longer an actionable target and may geometrically cover the entire reward stack.</summary>
    public bool IgnoreCompletedUltimatumAnchorOcclusion { get; set; }

    public string Name { get; }
    public BehaviorStatus LastStatus { get; private set; } = BehaviorStatus.Failure;
    public string LastDecision { get; private set; } = "init";
    public nint CurrentTargetAddress => _lastTargetAddress;

    public LootClosestVisible(string name, InteractSystem interact, Func<GameSnapshot?> liveSnapshot)
    {
        Name = name; _interact = interact; _liveSnapshot = liveSnapshot;
    }

    public BehaviorStatus Tick(BehaviorContext ctx)
    {
        FinalizePendingPickup();
        _interact.Tick();

        if (_interact.IsBusy)
        {
            LastDecision = $"interact busy: {_interact.Current?.Description}";
            return LastStatus = BehaviorStatus.Running;
        }

        var snap = ctx.Snapshot;
        var player = snap.Player;
        if (player is null) { LastDecision = "no player"; return LastStatus = BehaviorStatus.Failure; }

        // Pick best candidate.
        GroundLabelView? best = null;
        string bestKind = "?";
        string bestReason = "";
        LootEvaluation bestEvaluation = new(true, "unpriced", 0, "unpriced");
        string bestItemName = "";
        int bestStackCount = 1;
        int bestOccupiedCells = 1;
        float bestDist = float.PositiveInfinity;
        int total = 0, targets = 0, visible = 0, inRange = 0, onScreen = 0, filtered = 0, terrainBlocked = 0;
        string lastSkipReason = "";

        var filter = SharedValueFilter;
        var lootSettings = ctx.Settings.Loot;

        foreach (var label in snap.GroundLabels)
        {
            total++;

            // Categorize: item / chest / door. AutoExile uses EntityType.Chest which maps
            // 1:1 with PoE's "Metadata/Chests/" prefix — that covers strongboxes
            // (StrongBoxes/*), heist (LeagueHeist/HeistChest*), blight (Blight*), breach,
            // expedition, sanctum, ritual rewards, etc. Doors use a path-substring check
            // to catch labyrinth/quest doors that surface a ground label. Zone transitions
            // are deliberately excluded — they'd zone the player out unexpectedly.
            var outerPath = label.Path;
            var isItem  = label.IsItem;
            var isChest = !isItem && outerPath.StartsWith("Metadata/Chests/", StringComparison.Ordinal);
            var isDoor  = !isItem && !isChest && outerPath.Contains("/Door", StringComparison.OrdinalIgnoreCase);
            if (!isItem && !isChest && !isDoor) continue;
            targets++;

            if (!label.IsLabelVisible) continue;
            visible++;

            if (_blacklist.Contains(label.LabelAddress)) continue;
            if (label.EntityGridPosition is { } spot && IsSpotAbandoned(spot)) continue;

            // Value filter only applies to items. Chests are gated by the render-name
            // denylist (default skips "Chest" trash containers; Strongboxes/HeistChests/
            // BlightChests have distinctive names so they still pass). Doors always pass.
            string takeReason = isItem ? "" : isChest ? "chest (always)" : "door (always)";
            LootEvaluation evaluation = new(true, takeReason, 0, isItem ? "unpriced" : bestKind);
            if (isChest && lootSettings.ChestRenderNameDenylist is { Count: > 0 } deny)
            {
                var renderName = label.RenderName;
                if (!string.IsNullOrEmpty(renderName))
                {
                    var skipMe = false;
                    foreach (var n in deny)
                    {
                        if (string.IsNullOrWhiteSpace(n)) continue;
                        if (string.Equals(renderName, n.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            skipMe = true;
                            lastSkipReason = $"chest '{renderName}' denylisted";
                            break;
                        }
                    }
                    if (skipMe && !BypassValueFilter) { filtered++; continue; }
                    takeReason = $"chest '{renderName}'";
                }
            }
            if (isItem && filter is not null)
            {
                var eval = filter.Evaluate(label, lootSettings);
                if (!eval.ShouldTake && !BypassValueFilter)
                {
                    filtered++;
                    lastSkipReason = eval.Reason;
                    continue;
                }
                takeReason = BypassValueFilter ? "manual loot (filters bypassed)" : eval.Reason;
                evaluation = eval;
            }

            var d = label.DistanceToPlayer;
            var interactionRange = MathF.Min(ClickRangeGrid, ctx.Settings.LootRangeGrid);
            if (float.IsInfinity(d) || d > interactionRange) continue;
            inRange++;

            // Screen distance alone can put a label inside nominal grab range on the other
            // side of a wall. Targeting terrain is the click-access oracle: route around
            // first, then click only when the straight segment is usable.
            if (label.EntityGridPosition is not { } labelGrid) continue;
            var targeting = snap.Nav.TargetingReader;
            if (!BypassTerrainLineOfSight
                && targeting is not null
                && !PathSmoother.HasLineOfSight(targeting,
                    player.GridPosition.X, player.GridPosition.Y,
                    labelGrid.X, labelGrid.Y,
                    minValue: 1))
            {
                terrainBlocked++;
                continue;
            }

            if (!label.IsRectOnScreen) continue;
            onScreen++;

            if (d >= bestDist) continue;
            best = label;
            bestDist = d;
            bestKind = isItem ? "item" : isChest ? "chest" : "door";
            bestReason = takeReason;
            if (isItem)
            {
                bestEvaluation = evaluation;
                bestItemName = label.ItemName;
                bestStackCount = Math.Max(1, label.StackCount);
                bestOccupiedCells = Math.Max(1, label.InventorySlots);
            }
        }

        if (best is null)
        {
            _lastTargetAddress = 0;
            _attemptsOnTarget = 0;
            var skipNote = filtered > 0 ? $" filtered={filtered} ({lastSkipReason})" : "";
            LastDecision = $"no candidate (total={total}, targets={targets}, vis={visible}, inRange={inRange}, blocked={terrainBlocked}, onScreen={onScreen}{skipNote})";
            return LastStatus = BehaviorStatus.Failure;
        }

        // New target → reset attempt counter.
        if (best.LabelAddress != _lastTargetAddress)
        {
            _lastTargetAddress = best.LabelAddress;
            _attemptsOnTarget = 0;
        }

        // Display name: items use the inner WorldItem path; chests/doors expose nothing
        // inner so the outer label path is what reads usefully (e.g. "StrongBoxArtifact").
        var displayPath = best.IsItem ? best.InnerItemPath : best.Path;
        var targetAddr = best.LabelAddress;
        if (best.IsItem
            && BotMonotonicClock.ElapsedSince(_lastItemClickAt).TotalMilliseconds < MinItemClickIntervalMs)
        {
            LastDecision = $"pickup cadence ({MinItemClickIntervalMs}ms minimum)";
            return LastStatus = BehaviorStatus.Running;
        }
        // Occlusion set: any other visible label overlapping the target, plus the on-ground
        // Ritual Rewards button. A center click on a covered label hits the covering element
        // instead (live 2026-07-15: stacked decks behind the Rituals button were unlootable).
        var avoid = BuildOcclusionRects(snap, best, out var coveredByPersistent, out var persistentName);
        if (best.LabelRect is { } bestRect
            && InteractSystem.FindUncoveredPoint(bestRect, avoid) is null)
        {
            // Every candidate point is covered — clicking would hit the wrong element.
            if (coveredByPersistent && best.EntityGridPosition is { } coveredGrid)
            {
                // Covered by a structure label (essence monolith, strongbox, door, Ritual
                // Rewards button) that never clears by looting. Time the STABLE grid position,
                // not the address: the label re-mints every time it scrolls off-screen and back,
                // so an address blacklist never sticks (live 2026-07-16: a Glassblower's Bauble
                // fully under a 530x212 essence-Monolith label got re-approached every pass).
                // After PersistentCoverGiveUpSeconds, write the spot off for the whole map.
                if (_coverTimerPos is not { } tp || !WithinAbandonRadius(tp, coveredGrid))
                {
                    _coverTimerPos = coveredGrid;
                    _coverTimerSince = BotMonotonicClock.Now;
                    LastDecision = $"{bestKind} {ShortPath(displayPath)} covered by {persistentName} — timing give-up";
                }
                else if (BotMonotonicClock.ElapsedSince(_coverTimerSince).TotalSeconds >= PersistentCoverGiveUpSeconds)
                {
                    _abandonedSpots.Add(coveredGrid);
                    _coverTimerPos = null;
                    _coverTimerSince = TimeSpan.MinValue;
                    _attemptsOnTarget = 0;
                    LastDecision = $"abandoned spot ({coveredGrid.X},{coveredGrid.Y}) — " +
                        $"{ShortPath(displayPath)} persistently covered by {persistentName}";
                }
                else
                    LastDecision = $"{bestKind} {ShortPath(displayPath)} covered by {persistentName} " +
                        $"({BotMonotonicClock.ElapsedSince(_coverTimerSince).TotalSeconds:F1}s)";
                return LastStatus = BehaviorStatus.Failure;
            }
            // Covered only by other item labels — transient (they clear as the top item is
            // looted). Burn an attempt so a genuinely buried label still blacklists by address
            // instead of stalling the sweep; layouts shift as the bot moves, so early attempts
            // often free up.
            _attemptsOnTarget++;
            if (_attemptsOnTarget >= MaxAttemptsBeforeBlacklist)
            {
                _blacklist.Add(targetAddr);
                LastDecision = $"blacklisted {bestKind} {ShortPath(displayPath)} — label fully covered";
                _attemptsOnTarget = 0;
            }
            else
                LastDecision = $"label fully covered by {avoid.Count} rects (attempt {_attemptsOnTarget})";
            return LastStatus = BehaviorStatus.Failure;
        }

        var ticket = _interact.Begin(best, ctx.Input, snap.Window,
            verify: () =>
            {
                var current = _liveSnapshot();
                if (current is null) return true;
                foreach (var l in current.GroundLabels)
                    if (l.LabelAddress == targetAddr) return false;
                return true;
            },
            description: $"{bestKind} {ShortPath(displayPath)} d={bestDist:F1}",
            timeoutMs: ClickTimeoutMs,
            avoid: avoid);

        if (ticket is null)
        {
            LastDecision = "click suppressed (gate)";
            return LastStatus = BehaviorStatus.Failure;
        }

        if (best.IsItem)
        {
            _lastItemClickAt = BotMonotonicClock.Now;
            _pendingPickup = new PendingPickup(
                ticket, bestItemName, bestStackCount, bestOccupiedCells, bestEvaluation);
        }

        _attemptsOnTarget++;
        if (_attemptsOnTarget >= MaxAttemptsBeforeBlacklist)
        {
            // The verify predicate fires when the label disappears — if we hit the cap, the
            // last N clicks were all timeouts (label still present). Blacklist for the area.
            _blacklist.Add(targetAddr);
            LastDecision = $"blacklisted {bestKind} {ShortPath(displayPath)} after {_attemptsOnTarget} attempts";
            _attemptsOnTarget = 0;
            return LastStatus = BehaviorStatus.Running;
        }

        var reasonNote = string.IsNullOrEmpty(bestReason) ? "" : $" — {bestReason}";
        LastDecision = $"clicked {bestKind} {ShortPath(displayPath)} d={bestDist:F1} attempt {_attemptsOnTarget}{reasonNote}";
        return LastStatus = BehaviorStatus.Running;
    }

    /// <summary>Whether this label was written off after repeated failed clicks — shared with
    /// the mode's sweep gate so it doesn't keep steering toward a label we gave up on.</summary>
    public bool IsBlacklistedLabel(nint labelAddress) => _blacklist.Contains(labelAddress);

    /// <summary>Externally write off a label for this area — used by the mode's loot sweep
    /// when this behavior persistently refuses a label the sweep selected (LOS-blocked,
    /// off-screen, occluded) so target selection moves on instead of stalling.</summary>
    public void BlacklistLabel(nint labelAddress)
    {
        if (labelAddress != 0) _blacklist.Add(labelAddress);
    }

    /// <summary>Rects that may sit on top of <paramref name="target"/>'s label: every other
    /// visible ground label that overlaps it, plus the on-ground Ritual Rewards button.
    /// <paramref name="coveredByPersistent"/> is set when any overlapping occluder is a
    /// structure label (non-item: essence monolith, chest/strongbox, door, barrel) or the
    /// Ritual Rewards button — covers that looting the target can never remove.</summary>
    private IReadOnlyList<ElementGeometry.Rect> BuildOcclusionRects(
        GameSnapshot snap, GroundLabelView target,
        out bool coveredByPersistent, out string persistentName)
    {
        coveredByPersistent = false;
        persistentName = "";
        var rects = new List<ElementGeometry.Rect>();
        if (target.LabelRect is not { } t) return rects;
        foreach (var l in snap.GroundLabels)
        {
            if (l.LabelAddress == target.LabelAddress) continue;
            if (!l.IsLabelVisible) continue;
            if (IgnoreCompletedUltimatumAnchorOcclusion
                && !l.IsItem
                && l.Path.Contains("UltimatumChallengeInteractable", StringComparison.OrdinalIgnoreCase))
                continue;
            if (l.LabelRect is not { } r || !t.Overlaps(r)) continue;
            rects.Add(r);
            if (!l.IsItem && !coveredByPersistent)
            {
                coveredByPersistent = true;
                persistentName = !string.IsNullOrEmpty(l.RenderName) ? l.RenderName : ShortPath(l.Path);
            }
            if (rects.Count >= 24) break;
        }
        var rewards = snap.RitualRewardsButton;
        if (rewards.IsVisible && rewards.ClickRect is { } rr && t.Overlaps(rr))
        {
            rects.Add(rr);
            if (!coveredByPersistent) { coveredByPersistent = true; persistentName = "Ritual Rewards"; }
        }
        return rects;
    }

    /// <summary>Whether a drop at <paramref name="grid"/> was written off for the area because a
    /// persistent structure label sits fully on top of it. Shared with the mode's sweep gate and
    /// LootMemory so neither keeps steering toward an unlootable spot.</summary>
    public bool IsSpotAbandoned(Vector2i grid)
    {
        foreach (var s in _abandonedSpots)
            if (WithinAbandonRadius(s, grid)) return true;
        return false;
    }

    /// <summary>Positions written off this area by the persistent-cover give-up. Synced into
    /// LootMemory so the end-of-map backtrack doesn't walk to an unlootable spot.</summary>
    public IReadOnlyList<Vector2i> AbandonedSpots => _abandonedSpots;

    private static bool WithinAbandonRadius(Vector2i a, Vector2i b)
    {
        long dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy <= (long)AbandonRadiusGrid * AbandonRadiusGrid;
    }

    public void Reset()
    {
        _interact.Cancel();
        _blacklist.Clear();
        _abandonedSpots.Clear();
        _coverTimerPos = null;
        _coverTimerSince = TimeSpan.MinValue;
        _lastTargetAddress = 0;
        _attemptsOnTarget = 0;
        _pendingPickup = null;
        _lastItemClickAt = TimeSpan.MinValue;
        LastStatus = BehaviorStatus.Failure;
    }

    private void FinalizePendingPickup()
    {
        if (_pendingPickup is not { } pending || !pending.Ticket.IsResolved) return;
        if (pending.Ticket.Input.Token?.Outcome == Input.ActionOutcome.Confirmed)
        {
            SharedLedger?.Record(pending.Name, pending.StackCount, pending.Evaluation);
            PickupConfirmed?.Invoke(new ConfirmedPickup(
                pending.Name, pending.StackCount, pending.OccupiedCells, pending.Evaluation));
        }
        _pendingPickup = null;
    }

    private static string ShortPath(string path)
    {
        var i = path.LastIndexOf('/');
        return i >= 0 ? path[(i + 1)..] : path;
    }

    /// <summary>
    /// Process-wide value filter. Set once by <see cref="BotApp"/>; reads the live PriceCatalog
    /// + LootSettings on every Evaluate call. Null = loot mode runs without any value gate
    /// (items pass straight through to range/visibility checks).
    /// </summary>
    public static ValueFilter? SharedValueFilter { get; set; }
    public static LootLedger? SharedLedger { get; set; }
}
