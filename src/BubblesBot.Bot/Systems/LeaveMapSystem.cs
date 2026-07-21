using BubblesBot.Bot.Behaviors;
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
    public enum Phase { Idle, CastPortal, EnterPortal, Done, Failed }
    public enum Result { InProgress, Succeeded, Failed }

    public Phase CurrentPhase { get; private set; } = Phase.Idle;
    public string Status { get; private set; } = "idle";
    public bool IsBusy => CurrentPhase is not (Phase.Idle or Phase.Done or Phase.Failed);

    private readonly MovementSystem _movement;
    private readonly Func<int>      _getPortalVk;
    private readonly Func<BehaviorContext, bool> _isExpectedDestination;
    private readonly AreaTransitionTracker _transition = new();

    private TimeSpan _phaseStartedAt;
    private TimeSpan _lastActionAt;
    private uint     _portalEntityId;
    private int      _castAttempts;
    private uint     _startAreaHash;
    private TimeSpan _portalInRangeSince;

    private const int ActionCooldownMs    = 600;
    private const int MaxCastAttempts     = 4;
    private const int PhaseTimeoutSeconds = 20;
    private const int CastTimeoutSeconds  = 12;

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
        Func<BehaviorContext, bool> isExpectedDestination)
    {
        _movement    = movement;
        _getPortalVk = getPortalVk;
        _isExpectedDestination = isExpectedDestination;
    }

    public void Start(BehaviorContext ctx)
    {
        CurrentPhase    = Phase.CastPortal;
        _phaseStartedAt = BotMonotonicClock.Now;
        _lastActionAt   = TimeSpan.Zero;
        _portalEntityId = 0;
        _castAttempts   = 0;
        _startAreaHash  = ctx.Snapshot.AreaHash;
        _portalInRangeSince = TimeSpan.MinValue;
        _transition.Start(_startAreaHash, AreaRole.Map, AreaRole.SafeHub, AreaTransitionTracker.MonotonicNow());

        Status = "opening town portal";
        BubblesBot.Bot.Diagnostics.EventLog.Log("LeaveMap", "started");
    }

    public void Cancel()
    {
        CurrentPhase = Phase.Idle;
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
            ctx.Snapshot.AreaHash, observedRole, AreaTransitionTracker.MonotonicNow());
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

        var timeout = CurrentPhase == Phase.CastPortal ? CastTimeoutSeconds : PhaseTimeoutSeconds;
        if ((BotMonotonicClock.Now - _phaseStartedAt).TotalSeconds > timeout)
            return Fail($"timeout in {CurrentPhase}: {Status}");

        if ((BotMonotonicClock.Now - _lastActionAt).TotalMilliseconds < ActionCooldownMs)
            return Result.InProgress;

        return CurrentPhase switch
        {
            Phase.CastPortal  => TickCast(ctx),
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
            _portalEntityId = portal.Id;
            return Advance(Phase.EnterPortal, $"town portal id={portal.Id} — entering");
        }

        if (_castAttempts >= MaxCastAttempts)
        {
            LogPortalCensus(ctx); // ground truth for "the user saw a portal but we didn't"
            return Fail("no town portal after tapping portal key — likely out of portal scrolls");
        }

        var vk = _getPortalVk();
        if (vk == 0) return Fail("portal key unbound");

        var ticket = ctx.Input.VerifiedTapKey(vk, Input.ClickIntent.UseSkill, "use portal scroll",
            expectResolved: () => FindNewTownPortal(ctx) is not null, timeoutMs: 2500);
        if (ticket.Accepted)
        {
            _castAttempts++;
            _lastActionAt = BotMonotonicClock.Now;
            Status = $"tapped portal key ({_castAttempts}/{MaxCastAttempts})";
            BubblesBot.Bot.Diagnostics.EventLog.Log("LeaveMap", $"portal key tapped (vk=0x{vk:X})");
        }
        return Result.InProgress;
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

    private static float Distance(Vector2i a, Vector2i b)
    {
        var dx = (float)(a.X - b.X);
        var dy = (float)(a.Y - b.Y);
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
