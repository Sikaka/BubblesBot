using BubblesBot.Bot.Behaviors;
using BubblesBot.Bot.Input;
using BubblesBot.Bot.Settings;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Systems;

/// <summary>
/// Selects an exact visible stash-tab label with hover ancestry, then confirms the switch by the
/// visible-content pointer changing (each tab's content root is a distinct element). We do NOT
/// cross-check a server display index: it does not track click/keyboard tab changes on migrated
/// stashes (validated live 2026-07-23 — the field stayed on the last *clicked* tab while the active
/// tab moved). Because clicking an already-active tab is a no-op, "content changed" OR "content
/// stayed put after clicking the target's own label" both mean we are on the target — which is
/// unambiguous for a uniquely-named tab. Duplicate names remain best-effort (any same-named switch).
/// </summary>
public sealed class StashTabSwitcher
{
    public enum Result { InProgress, Succeeded, Failed }

    private const int MaxClickAttempts = 4;
    private const int UniqueLabelFallbackMs = 1200;
    // After clicking the target's label, the content settling this long with no change means the
    // target was already the active tab (clicking it did nothing) — treat as selected.
    private const int ConfirmSettleMs = 450;
    // When the target label isn't rendered, cycle the active root tab (Ctrl+Left) to bring the root
    // strip back into view. Bounded so a genuinely-missing name still fails closed.
    private const int MaxReachSteps = 24;
    private const int ReachThrottleMs = 300;
    private const int VkLeftArrow = 0x25;
    private static readonly int[] CtrlModifier = { 0x11 };
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly Func<GameSnapshot?> _getSnapshot;
    private string _targetName = string.Empty;
    private bool _requireGeneralPurpose;
    private TimeSpan _startedAt;
    private TimeSpan _lastActionAt = TimeSpan.MinValue;
    private int _clickAttempts;
    private int _hoverCandidateIndex;
    private TimeSpan _hoverStartedAt = TimeSpan.MinValue;
    private nint _hoverTarget;
    // Switch-confirmation state: the visible-content pointer captured just before our click, and
    // whether we have clicked the target's label at least once.
    private nint _preClickVisible;
    private bool _clickedTarget;
    private int _reachSteps;
    private TimeSpan _lastReachAt = TimeSpan.MinValue;

    public string TargetName => _targetName;
    public string Status { get; private set; } = "idle";
    public bool IsStarted => _targetName.Length > 0;

    public StashTabSwitcher(Func<GameSnapshot?> getSnapshot)
        => _getSnapshot = getSnapshot;

    public void Start(string targetName, bool requireGeneralPurpose)
    {
        _targetName = targetName.Trim();
        _requireGeneralPurpose = requireGeneralPurpose;
        _startedAt = BotMonotonicClock.Now;
        _lastActionAt = TimeSpan.MinValue;
        _clickAttempts = 0;
        _hoverCandidateIndex = 0;
        _hoverStartedAt = TimeSpan.MinValue;
        _hoverTarget = 0;
        _preClickVisible = 0;
        _clickedTarget = false;
        _reachSteps = 0;
        _lastReachAt = TimeSpan.MinValue;
        Status = $"locating tab '{_targetName}'";
    }

    public void Reset()
    {
        _targetName = string.Empty;
        _clickAttempts = 0;
        _hoverCandidateIndex = 0;
        _hoverStartedAt = TimeSpan.MinValue;
        _hoverTarget = 0;
        _preClickVisible = 0;
        _clickedTarget = false;
        _reachSteps = 0;
        _lastReachAt = TimeSpan.MinValue;
        Status = "idle";
    }

    public Result Tick(BehaviorContext ctx)
    {
        if (!ctx.Snapshot.IsStashOpen)
            return Fail("stash is closed");
        if (_targetName.Length == 0)
            return Fail("target tab is empty");
        if (BotMonotonicClock.ElapsedSince(_startedAt).TotalSeconds
            > LatencyPolicy.TimeoutSeconds(Timeout.TotalSeconds, ctx.Settings))
            return Fail($"timeout switching to '{_targetName}'");

        var catalog = ctx.Snapshot.StashTabs;
        var target = catalog.Find(_targetName, _requireGeneralPurpose);
        if (target is null)
        {
            // ServerData.PlayerStashTabs commonly lags the first visible stash frame,
            // especially on long-lived Standard accounts with hundreds of remove-only tabs.
            // The old immediate failure stopped a valid run before the catalog populated.
            // Keep polling inside the already-bounded switch timeout; a truly missing name
            // still fails closed when that deadline expires.
            Status = catalog.Tabs.Count == 0
                ? $"waiting for stash tab catalog before locating '{_targetName}'"
                : $"waiting for {(_requireGeneralPurpose ? "general-purpose " : "")}stash tab '{_targetName}' ({catalog.Tabs.Count} catalogued)";
            return Result.InProgress;
        }

        var currentVisible = ctx.Snapshot.StashInventory.VisibleStashAddress;
        var nameCount = catalog.Tabs.Count(
            t => t.Name.Equals(_targetName, StringComparison.OrdinalIgnoreCase));

        // Confirmation: we clicked the target's own label. If the visible content switched, we
        // moved onto it; if it stayed put after the click settled, the target was already active
        // (clicking it is a no-op). Either way we are on the target — unambiguous for a unique name.
        if (_clickedTarget && currentVisible != 0)
        {
            var switched = _preClickVisible != 0 && currentVisible != _preClickVisible;
            var settled = BotMonotonicClock.ElapsedSince(_lastActionAt).TotalMilliseconds >= ConfirmSettleMs;
            if (switched || settled)
            {
                EmitSelected(target, currentVisible, switched, ambiguous: nameCount > 1);
                return Result.Succeeded;
            }
            Status = $"confirming '{target.Name}' (content {(switched ? "changed" : "settling")})";
            return Result.InProgress;
        }
        if (currentVisible == 0)
        {
            Status = "waiting for visible stash content";
            return Result.InProgress;
        }
        var maxClickAttempts = LatencyPolicy.RetryLimit(MaxClickAttempts, ctx.Settings);
        if (_clickAttempts >= maxClickAttempts)
            return Fail($"click limit selecting '{target.Name}'");
        if (BotMonotonicClock.ElapsedSince(_lastActionAt).TotalMilliseconds < 250)
            return Result.InProgress;

        var candidates = ctx.Snapshot.StashTabStrip.FindExact(target.Name)
            .Where(candidate => candidate.Rect.CenterX >= 0
                && candidate.Rect.CenterX < ctx.Snapshot.Window.Width
                && candidate.Rect.CenterY >= 0
                && candidate.Rect.CenterY < ctx.Snapshot.Window.Height)
            .ToArray();
        if (candidates.Length == 0)
        {
            // Label isn't rendered — we're viewing a different tab set (e.g. inside a folder/sub-tab
            // context). Cycle the active ROOT tab with Ctrl+Left (validated live to move the active
            // tab) to bring the root strip back so the label becomes clickable. Bounded + throttled.
            if (_reachSteps >= MaxReachSteps)
                return Fail($"could not reveal stash tab label '{target.Name}' after {_reachSteps} root-tab steps");
            if (BotMonotonicClock.ElapsedSince(_lastReachAt).TotalMilliseconds < ReachThrottleMs)
                return Result.InProgress;
            var reach = ctx.Input.VerifiedModifierTapKey(
                VkLeftArrow, CtrlModifier, ClickIntent.InteractUi,
                $"reveal stash tab '{target.Name}' (Ctrl+Left)",
                expectResolved: () => _getSnapshot() is { IsStashOpen: true },
                timeoutMs: 800);
            if (reach.Accepted)
            {
                _reachSteps++;
                _lastReachAt = BotMonotonicClock.Now;
                Status = $"revealing '{target.Name}' (root-tab step {_reachSteps}/{MaxReachSteps})";
            }
            return Result.InProgress;
        }

        // Duplicate tab names are common after migrations. Try each exact visible label in
        // deterministic left-to-right order; only the server-authoritative display index can
        // confirm that the requested duplicate was selected.
        var candidate = candidates[_hoverCandidateIndex % candidates.Length];
        var rect = candidate.Rect;
        var (sx, sy) = ctx.Snapshot.Window.ToScreen((int)rect.CenterX, (int)rect.CenterY);
        var hoverVerified = HoverResolvesTo(ctx.Snapshot, candidate.Element);
        if (_hoverTarget != candidate.Element || !hoverVerified)
        {
            // Live Standard research: tab-label controls can be exact, visible descendants of
            // the stash strip while IngameState.UIHover remains zero indefinitely. A single
            // exact visible candidate is still unambiguous; after a settle window, allow the
            // verified click and require the server-authoritative selected display index to
            // change. Duplicate visible labels continue to require hover ancestry.
            var uniqueFallbackReady = candidates.Length == 1
                && _hoverTarget == candidate.Element
                && _hoverStartedAt != TimeSpan.MinValue
                && BotMonotonicClock.ElapsedSince(_hoverStartedAt).TotalMilliseconds >= UniqueLabelFallbackMs;
            if (uniqueFallbackReady)
            {
                Status = $"selecting unique exact tab label '{target.Name}' without hover telemetry";
            }
            else
            {
            if (_hoverTarget == candidate.Element
                && _hoverStartedAt != TimeSpan.MinValue
                && BotMonotonicClock.ElapsedSince(_hoverStartedAt).TotalMilliseconds >= 500)
            {
                if (candidates.Length > 1)
                {
                    _hoverCandidateIndex = (_hoverCandidateIndex + 1) % candidates.Length;
                    _hoverTarget = 0;
                    _hoverStartedAt = TimeSpan.MinValue;
                    Status = $"trying another visible '{target.Name}' tab label";
                    return Result.InProgress;
                }
            }
            _hoverTarget = candidate.Element;
            if (_hoverStartedAt == TimeSpan.MinValue)
                _hoverStartedAt = BotMonotonicClock.Now;
            ctx.Input.HoverAt(sx, sy, CursorPriority.Halt);
            Status = $"verifying exact tab label '{target.Name}'";
            return Result.InProgress;
            }
        }

        _preClickVisible = currentVisible;
        var ticket = ctx.Input.Click(
            sx, sy, ClickIntent.InteractUi,
            $"select exact stash tab '{target.Name}'",
            // Resolve as soon as the visible content switches (fast path when not already active).
            // An already-active target won't change and is confirmed by the settle check above.
            expectResolved: () => _getSnapshot() is { } live
                && live.IsStashOpen
                && live.StashInventory.VisibleStashAddress != _preClickVisible
                && live.StashInventory.VisibleStashAddress != 0,
            timeoutMs: 1800);
        if (ticket.Accepted)
        {
            _clickAttempts++;
            _clickedTarget = true;
            _lastActionAt = BotMonotonicClock.Now;
            _hoverCandidateIndex = (_hoverCandidateIndex + 1) % candidates.Length;
            _hoverTarget = 0;
            _hoverStartedAt = TimeSpan.MinValue;
            Status = $"selecting '{target.Name}' ({_clickAttempts}/{maxClickAttempts})";
        }
        return Result.InProgress;
    }

    private void EmitSelected(StashTabsView.Tab target, nint visible, bool switched, bool ambiguous)
    {
        Status = ambiguous
            ? $"on a tab named '{target.Name}' (duplicate name — best-effort)"
            : $"on tab '{target.Name}'";
        BubblesBot.Bot.Diagnostics.EventLog.Emit(
            "stash", "stash.tab-selected",
            BubblesBot.Bot.Diagnostics.EventSeverity.Info,
            Status,
            new Dictionary<string, object?>
            {
                ["name"] = target.Name,
                ["type"] = target.Type,
                ["visibleContent"] = $"0x{visible:X}",
                ["switched"] = switched,
                ["ambiguousDuplicate"] = ambiguous,
                ["generalPurposeRequired"] = _requireGeneralPurpose,
            });
    }

    private static bool HoverResolvesTo(GameSnapshot snapshot, nint target)
    {
        if (!snapshot.Reader.TryReadStruct<nint>(
                snapshot.IngameStateAddress + BubblesBot.Core.Game.KnownOffsets.IngameState.UIHover,
                out var current))
            return false;
        for (var depth = 0; depth < 24 && current != 0; depth++)
        {
            if (current == target) return true;
            if (!snapshot.Reader.TryReadStruct<nint>(
                    current + BubblesBot.Core.Game.KnownOffsets.Element.Parent, out var parent)
                || parent == current)
                break;
            current = parent;
        }
        return false;
    }

    private Result Fail(string reason)
    {
        Status = reason;
        return Result.Failed;
    }
}
