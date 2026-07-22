using BubblesBot.Bot.Behaviors;
using BubblesBot.Bot.Behaviors.Movement;
using BubblesBot.Bot.Behaviors.Interact;
using BubblesBot.Bot.Modes;
using BubblesBot.Bot.Settings;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Systems;

/// <summary>
/// Leaves the current map and returns to the hideout/town: tap the Portal-Scroll key (F by
/// default) to open a Town Portal, wait for the spawned <c>TownPortal</c> entity, walk into
/// it, and confirm the area changed. Mirrors <see cref="MapDeviceSystem"/>'s phase shape so
/// the stacked-deck orchestrator can drive both the same way.
///
/// <para><b>Out-of-resources detection.</b> If the portal key is tapped
/// <see cref="MaxCastAttempts"/> times and no new town portal ever spawns, the most likely
/// cause is an empty Portal-Scroll stack (or an unbound key). The system fails with that
/// status and the orchestrator turns it into a "stop — out of mapping resources" condition.
/// This avoids needing the inventory panel open mid-map just to count scrolls.</para>
/// </summary>
public sealed class LeaveMapSystem
{
    public enum Phase { Idle, CastPortal, ReturnToEntrance, EnterPortal, Done, Failed }
    public enum Result { InProgress, Succeeded, Failed }

    public Phase CurrentPhase { get; private set; } = Phase.Idle;
    public string Status { get; private set; } = "idle";
    public bool IsBusy => CurrentPhase is not (Phase.Idle or Phase.Done or Phase.Failed);

    private readonly MovementSystem _movement;
    private readonly Func<int>      _getPortalVk;
    private readonly Func<BehaviorContext, bool> _isExpectedDestination;
    private readonly FollowPath? _returnToEntrance;
    private readonly EnterAreaTransition? _returnThroughArenaExit;
    private readonly AreaTransitionTracker _transition = new();

    private TimeSpan _phaseStartedAt;
    private TimeSpan _lastActionAt;
    private uint     _portalEntityId;
    private int      _castAttempts;
    private uint     _startAreaHash;
    private TimeSpan _portalInRangeSince;
    private bool     _inventoryFallbackStarted;
    private int      _inventoryScrollAttempts;
    private Vector2i? _returnAnchor;
    private Vector2i? _returnGoal;
    private TimeSpan _returnAnchorReachedAt;
    private readonly InteractSystem _returnInteract = new();

    private const int ActionCooldownMs    = 600;
    private const int MaxCastAttempts     = 4;
    private const int PhaseTimeoutSeconds = 20;
    private const int CastTimeoutSeconds  = 12;
    private const int ReturnTimeoutSeconds = 180;
    private const int InventoryKeyVk       = 0x49; // I
    private const int MaxInventoryScrollAttempts = 3;

    /// <summary>
    /// A town portal only counts as usable when it sits this close to the player. Scroll
    /// portals open at the caster's feet, so proximity — NOT entity-id novelty — is what
    /// makes a portal "ours": PoE relocates/reuses the multiplex portal entity, so the id
    /// cached at the map spawn reappears at the player after tapping, and a pre-flow id
    /// filter rejected our own freshly cast portal forever (live 2026-07-15, portal-census
    /// evidence). A pre-existing portal at our feet is equally valid — it goes to the same
    /// hideout and saves a scroll.
    /// </summary>
    private const float UsablePortalRangeGrid = 30f;

    public AreaTransitionState Transition => _transition.State;

    public LeaveMapSystem(MovementSystem movement, Func<int> getPortalVk,
        Func<BehaviorContext, bool> isExpectedDestination, SkillBook? skills = null,
        Func<GameSnapshot?>? getSnapshot = null)
    {
        _movement    = movement;
        _getPortalVk = getPortalVk;
        _isExpectedDestination = isExpectedDestination;
        if (skills is not null)
        {
            _returnToEntrance = new FollowPath(
                "return to original map portal", movement, _ => _returnGoal,
                skills, goalArrivalRadius: 12f, allowGapCrossing: true,
                preferDashForLongTravel: true);
            if (getSnapshot is not null)
            {
                _returnThroughArenaExit = new EnterAreaTransition(
                    "leave completed boss sub-area", _returnInteract, movement, skills,
                    getSnapshot, MapTransitionPolicy.IsTraversalCandidate,
                    allowGapCrossing: false);
            }
        }
    }

    public void Start(BehaviorContext ctx, Vector2i? returnAnchor = null)
    {
        CurrentPhase    = Phase.CastPortal;
        _phaseStartedAt = BotMonotonicClock.Now;
        _lastActionAt   = TimeSpan.Zero;
        _portalEntityId = 0;
        _castAttempts   = 0;
        _startAreaHash  = ctx.Snapshot.AreaHash;
        _portalInRangeSince = TimeSpan.MinValue;
        _inventoryFallbackStarted = false;
        _inventoryScrollAttempts = 0;
        _returnAnchor = returnAnchor;
        _returnGoal = returnAnchor;
        _returnAnchorReachedAt = TimeSpan.MinValue;
        _returnToEntrance?.Reset();
        _returnThroughArenaExit?.Reset();
        _transition.Start(_startAreaHash, AreaRole.Map, AreaRole.SafeHub, AreaTransitionTracker.MonotonicNow());

        Status = "opening town portal";
        BubblesBot.Bot.Diagnostics.EventLog.Log("LeaveMap", "started");
    }

    public void Cancel()
    {
        CurrentPhase = Phase.Idle;
        _returnToEntrance?.Reset();
        _returnThroughArenaExit?.Reset();
        _movement.Release();
        Status = "cancelled";
    }

    public Result Tick(BehaviorContext ctx)
    {
        if (!IsBusy)
            return CurrentPhase == Phase.Done   ? Result.Succeeded
                 : CurrentPhase == Phase.Failed ? Result.Failed
                 : Result.InProgress;

        var observedRole = _isExpectedDestination(ctx)
            ? AreaRole.SafeHub
            : WorldAreaClassifier.Classify(ctx);
        var transition = _transition.Observe(
            ctx.Snapshot.AreaHash, observedRole, AreaTransitionTracker.MonotonicNow(),
            TimeSpan.FromMilliseconds(LatencyPolicy.AllowanceMs(ctx.Settings)));
        if (transition.Outcome == AreaTransitionOutcome.Confirmed)
        {
            _movement.Release();
            CurrentPhase = Phase.Done;
            Status = "destination verified - left map";
            BubblesBot.Bot.Diagnostics.EventLog.Emit(
                "leave-map", "leave-map.destination-confirmed", BubblesBot.Bot.Diagnostics.EventSeverity.Info,
                "area changed and expected destination was observed",
                new Dictionary<string, object?>
                {
                    ["intentId"] = transition.IntentId,
                    ["fromAreaHash"] = transition.OriginAreaHash,
                    ["toAreaHash"] = transition.ObservedAreaHash,
                    ["observedRole"] = transition.ObservedRole.ToString(),
                });
            return Result.Succeeded;
        }
        if (transition.Outcome is AreaTransitionOutcome.UnexpectedDestination or AreaTransitionOutcome.TimedOut)
            return Fail($"transition {transition.Outcome}: expected {transition.ExpectedDestination}, " +
                        $"observed {transition.ObservedRole} at 0x{transition.ObservedAreaHash:X8}");
        if (transition.Outcome == AreaTransitionOutcome.VerifyingDestination)
        {
            Status = "area changed - verifying destination";
            return Result.InProgress;
        }

        var timeout = CurrentPhase switch
        {
            Phase.CastPortal => CastTimeoutSeconds,
            Phase.ReturnToEntrance => ReturnTimeoutSeconds,
            _ => PhaseTimeoutSeconds,
        };
        if ((BotMonotonicClock.Now - _phaseStartedAt).TotalSeconds
            > LatencyPolicy.TimeoutSeconds(timeout, ctx.Settings))
            return Fail($"timeout in {CurrentPhase}: {Status}");

        if ((BotMonotonicClock.Now - _lastActionAt).TotalMilliseconds < ActionCooldownMs)
            return Result.InProgress;

        return CurrentPhase switch
        {
            Phase.CastPortal  => TickCast(ctx),
            Phase.ReturnToEntrance => TickReturnToEntrance(ctx),
            Phase.EnterPortal => TickEnter(ctx),
            _ => Result.InProgress,
        };
    }

    // ─── Phases ──────────────────────────────────────────────────────────

    private Result TickCast(BehaviorContext ctx)
    {
        var portal = FindNewTownPortal(ctx);
        if (portal is not null)
        {
            if (ctx.Snapshot.Inventory.IsOpen)
            {
                var close = ctx.Input.TapKey(
                    InventoryKeyVk, Input.ClickIntent.InteractUi,
                    "close inventory after using Portal Scroll");
                if (close.Accepted)
                {
                    _lastActionAt = BotMonotonicClock.Now;
                    Status = "closing inventory before entering town portal";
                }
                return Result.InProgress;
            }
            _portalEntityId = portal.Id;
            return Advance(Phase.EnterPortal, $"town portal id={portal.Id} — entering");
        }

        var maxCastAttempts = LatencyPolicy.RetryLimit(MaxCastAttempts, ctx.Settings);
        if (_castAttempts >= maxCastAttempts)
        {
            return TickCastFromInventory(ctx);
        }

        var vk = _getPortalVk();
        if (vk == 0) return Fail("portal key unbound");

        var ticket = ctx.Input.VerifiedTapKey(vk, Input.ClickIntent.UseSkill, "use portal scroll",
            expectResolved: () => FindNewTownPortal(ctx) is not null, timeoutMs: 2500);
        if (ticket.Accepted)
        {
            _castAttempts++;
            _lastActionAt = BotMonotonicClock.Now;
            Status = $"tapped portal key ({_castAttempts}/{maxCastAttempts})";
            BubblesBot.Bot.Diagnostics.EventLog.Log("LeaveMap", $"portal key tapped (vk=0x{vk:X})");
        }
        return Result.InProgress;
    }

    private Result TickCastFromInventory(BehaviorContext ctx)
    {
        if (!_inventoryFallbackStarted)
        {
            _inventoryFallbackStarted = true;
            _phaseStartedAt = BotMonotonicClock.Now;
            Status = "portal hotkey produced no portal - trying carried Portal Scroll";
            Diagnostics.EventLog.Emit(
                "leave-map", "leave-map.inventory-scroll-fallback",
                Diagnostics.EventSeverity.Warning, Status);
        }

        if (!ctx.Snapshot.Inventory.IsOpen)
        {
            var open = ctx.Input.TapKey(
                InventoryKeyVk, Input.ClickIntent.InteractUi,
                "open inventory for Portal Scroll fallback");
            if (open.Accepted)
            {
                _lastActionAt = BotMonotonicClock.Now;
                Status = "opening inventory for Portal Scroll fallback";
            }
            return Result.InProgress;
        }

        var scroll = BestPortalScroll(ctx.Snapshot.Inventory.Items);
        if (scroll is not { Rect: { } rect })
        {
            LogPortalCensus(ctx);
            if (_returnAnchor is not null && _returnToEntrance is not null)
                return Advance(Phase.ReturnToEntrance,
                    "no Portal Scroll - returning to the original map entrance");
            return Fail("portal hotkey failed and no carried Portal Scroll was visible");
        }
        var maxInventoryScrollAttempts = LatencyPolicy.RetryLimit(
            MaxInventoryScrollAttempts, ctx.Settings, maxExtraAttempts: 4);
        if (_inventoryScrollAttempts >= maxInventoryScrollAttempts)
        {
            LogPortalCensus(ctx);
            return Fail("carried Portal Scroll did not create a town portal");
        }

        var point = ctx.Snapshot.Window.ToScreen((int)rect.CenterX, (int)rect.CenterY);
        var ticket = ctx.Input.RightClick(
            point.X, point.Y, Input.ClickIntent.InteractUi,
            "use carried Portal Scroll",
            expectResolved: () => FindNewTownPortal(ctx) is not null,
            timeoutMs: 2500);
        if (ticket.Accepted)
        {
            _inventoryScrollAttempts++;
            _lastActionAt = BotMonotonicClock.Now;
            Status = $"right-clicked carried Portal Scroll ({_inventoryScrollAttempts}/{maxInventoryScrollAttempts})";
        }
        return Result.InProgress;
    }

    private Result TickReturnToEntrance(BehaviorContext ctx)
    {
        if (_returnAnchor is not { } anchor || _returnToEntrance is null)
            return Fail("original map entrance was not recorded");

        if (ctx.Snapshot.Inventory.IsOpen)
        {
            var close = ctx.Input.TapKey(
                InventoryKeyVk, Input.ClickIntent.InteractUi,
                "close inventory before returning to original portal");
            if (close.Accepted)
            {
                _lastActionAt = BotMonotonicClock.Now;
                Status = "closing inventory before backtracking";
            }
            return Result.InProgress;
        }

        var portal = FindOriginalEntrancePortal(ctx, anchor);
        _returnGoal = portal?.GridPosition ?? anchor;

        var route = _returnToEntrance.Tick(ctx);
        Status = portal is null
            ? $"backtracking to map entrance: {_returnToEntrance.LastDecision}"
            : $"backtracking to original portal id={portal.Id}: {_returnToEntrance.LastDecision}";

        if (route == BehaviorStatus.Failure
            && _returnToEntrance.LastDecision == "no path"
            && _returnThroughArenaExit is not null
            && HasFreshTraversalTransition(ctx))
        {
            var exit = _returnThroughArenaExit.Tick(ctx);
            Status = "map entrance is on another sub-area blob - taking the local arena exit";
            if (exit == BehaviorStatus.Success)
            {
                _returnThroughArenaExit.Reset();
                _returnToEntrance.Reset();
                Status = "arena exit confirmed - resuming backtrack to map entrance";
            }
            return Result.InProgress;
        }

        if (route != BehaviorStatus.Success)
            return Result.InProgress;

        _movement.Release();
        if (portal is not null)
        {
            _portalEntityId = portal.Id;
            return Advance(Phase.EnterPortal, $"reached original map portal id={portal.Id} - entering");
        }

        if (_returnAnchorReachedAt == TimeSpan.MinValue)
        {
            _returnAnchorReachedAt = BotMonotonicClock.Now;
            Status = "at original map entrance - waiting for portal to stream";
            return Result.InProgress;
        }
        if ((BotMonotonicClock.Now - _returnAnchorReachedAt).TotalSeconds
            >= LatencyPolicy.TimeoutSeconds(5, ctx.Settings))
            return Fail("reached original map entrance but its return portal was absent");
        return Result.InProgress;
    }

    internal static InventoryView.Item? BestPortalScroll(
        IReadOnlyList<InventoryView.Item> items)
    {
        InventoryView.Item? best = null;
        foreach (var item in items)
        {
            if (item.Rect is null || !item.Path.Contains(
                    InventoryView.PortalScrollPathFragment,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            if (best is null || item.StackSize > best.Value.StackSize)
                best = item;
        }
        return best;
    }

    private Result TickEnter(BehaviorContext ctx)
    {
        if (ctx.Entities is null || ctx.Live is null) return Result.InProgress;

        EntityCache.Entry? portal = null;
        if (_portalEntityId != 0 && ctx.Entities.Entries.TryGetValue(_portalEntityId, out var p))
            portal = p;
        portal ??= FindNewTownPortal(ctx);
        if (portal is null) return Fail("town portal disappeared before entering");

        var dist = Distance(ctx.Live.Value.GridPosition, portal.GridPosition);
        if (dist > ctx.Settings.InteractionRangeGrid)
        {
            _portalInRangeSince = TimeSpan.MinValue;
            _movement.WalkToward(portal.GridPosition, new BehaviorContextLite(ctx.Snapshot, ctx.Input, ctx.Live));
            Status = $"walking to town portal (dist={dist:F0})";
            return Result.InProgress;
        }

        _movement.Release();
        if (_portalInRangeSince == TimeSpan.MinValue)
        {
            _portalInRangeSince = BotMonotonicClock.Now;
            Status = "in town-portal range - settling";
            return Result.InProgress;
        }
        if ((BotMonotonicClock.Now - _portalInRangeSince).TotalMilliseconds < 250)
            return Result.InProgress;

        // Multiplex town portals expose a proven clickable ground-label surface. Entity
        // bounds project near the portal base and can repeatedly miss even at point-blank
        // range, so prefer the visible label and retain projection only as a fallback.
        var label = ctx.Snapshot.GroundLabels.FirstOrDefault(candidate =>
            candidate.EntityId == portal.Id
            && candidate.IsLabelVisible
            && candidate.IsRectOnScreen
            && candidate.LabelRect is not null);
        (int X, int Y)? clickPoint = null;
        if (label?.LabelRect is { } rect)
        {
            var screen = ctx.Snapshot.Window.ToScreen((int)rect.CenterX, (int)rect.CenterY);
            clickPoint = (screen.X, screen.Y);
        }
        clickPoint ??= EntityClick.ResolveScreenPoint(ctx, portal);
        if (clickPoint is null) { Status = "no town portal click point"; return Result.InProgress; }

        var ticket = ctx.Input.Click(clickPoint.Value.X, clickPoint.Value.Y,
            Input.ClickIntent.InteractWorld, "enter town portal");
        if (ticket.Accepted)
        {
            _lastActionAt = BotMonotonicClock.Now;
            Status = "clicked town portal — waiting for area change";
        }
        return Result.InProgress;
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private Result Advance(Phase next, string status)
    {
        BubblesBot.Bot.Diagnostics.EventLog.Log("LeaveMap", $"phase {CurrentPhase} → {next}: {status}");
        CurrentPhase    = next;
        _phaseStartedAt = BotMonotonicClock.Now;
        _lastActionAt   = TimeSpan.Zero;
        _portalInRangeSince = TimeSpan.MinValue;
        Status          = status;
        return Result.InProgress;
    }

    private Result Fail(string reason)
    {
        BubblesBot.Bot.Diagnostics.EventLog.Emit(
            "leave-map", "leave-map.failed", BubblesBot.Bot.Diagnostics.EventSeverity.Error,
            reason,
            new Dictionary<string, object?>
            {
                ["phase"] = CurrentPhase.ToString(),
                ["startAreaHash"] = _startAreaHash,
                ["observedAreaHash"] = _transition.State.ObservedAreaHash,
                ["transitionOutcome"] = _transition.State.Outcome.ToString(),
                ["castAttempts"] = _castAttempts,
            });
        CurrentPhase = Phase.Failed;
        Status = reason;
        _movement.Release();
        return Result.Failed;
    }

    /// <summary>
    /// Failure forensics: dump every nearby entity that is portal-shaped OR has an
    /// empty/unknown identity (the 2026-07-15 miss was a portal hydrated mid-stream with an
    /// empty frozen path). One event, bounded size — only fires on the failure path.
    /// </summary>
    private static void LogPortalCensus(BehaviorContext ctx)
    {
        if (ctx.Entities is null || ctx.Live is null) return;
        var p = ctx.Live.Value.GridPosition;
        var lines = new List<string>();
        foreach (var e in ctx.Entities.Entries.Values)
        {
            long dx = e.GridPosition.X - p.X, dy = e.GridPosition.Y - p.Y;
            if (dx * dx + dy * dy > 60L * 60L) continue;
            var portalish = e.Path.Contains("Portal", StringComparison.OrdinalIgnoreCase)
                || e.Kind is EntityListReader.EntityKind.Portal or EntityListReader.EntityKind.TownPortal
                || e.Path.Length == 0;
            if (!portalish) continue;
            lines.Add($"id={e.Id} kind={e.Kind} stale={e.IsStale} grid=({e.GridPosition.X},{e.GridPosition.Y}) path='{e.Path}'");
            if (lines.Count >= 16) break;
        }
        BubblesBot.Bot.Diagnostics.EventLog.Emit(
            "leave-map", "leave-map.portal-census", BubblesBot.Bot.Diagnostics.EventSeverity.Warning,
            lines.Count == 0 ? "no portal-shaped or unidentified entities within 60 grid"
                             : string.Join(" | ", lines));
    }

    private EntityCache.Entry? FindNewTownPortal(BehaviorContext ctx)
    {
        if (ctx.Entities is null || ctx.Live is null) return null;
        var p = ctx.Live.Value.GridPosition;
        EntityCache.Entry? best = null;
        long bestD2 = (long)(UsablePortalRangeGrid * UsablePortalRangeGrid);
        foreach (var e in ctx.Entities.Entries.Values)
        {
            if (e.Kind != EntityListReader.EntityKind.TownPortal) continue;
            if (e.IsStale) continue;
            long dx = e.GridPosition.X - p.X;
            long dy = e.GridPosition.Y - p.Y;
            var d2 = dx * dx + dy * dy;
            if (d2 < bestD2) { bestD2 = d2; best = e; }
        }
        return best;
    }

    private static EntityCache.Entry? FindOriginalEntrancePortal(
        BehaviorContext ctx, Vector2i anchor)
    {
        if (ctx.Entities is null) return null;
        EntityCache.Entry? best = null;
        const long maxD2 = 80L * 80L;
        var bestD2 = maxD2;
        foreach (var e in ctx.Entities.Entries.Values)
        {
            if (e.IsStale) continue;
            if (e.Kind is not (EntityListReader.EntityKind.Portal or EntityListReader.EntityKind.TownPortal)
                && !e.Path.Contains("MultiplexPortal", StringComparison.OrdinalIgnoreCase))
                continue;
            long dx = e.GridPosition.X - anchor.X;
            long dy = e.GridPosition.Y - anchor.Y;
            var d2 = dx * dx + dy * dy;
            if (d2 < bestD2) { bestD2 = d2; best = e; }
        }
        return best;
    }

    private static bool HasFreshTraversalTransition(BehaviorContext ctx)
        => ctx.Entities?.Entries.Values.Any(e =>
            !e.IsStale && MapTransitionPolicy.IsTraversalCandidate(e)) == true;

    private static float Distance(Vector2i a, Vector2i b)
    {
        var dx = (float)(a.X - b.X);
        var dy = (float)(a.Y - b.Y);
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
