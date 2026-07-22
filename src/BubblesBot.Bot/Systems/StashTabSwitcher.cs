using BubblesBot.Bot.Behaviors;
using BubblesBot.Bot.Input;
using BubblesBot.Bot.Settings;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Systems;

/// <summary>
/// Selects an exact visible stash-tab label with hover ancestry and visible-index confirmation.
/// Keyboard arrows are intentionally not used: large Standard stashes group-jump rather than
/// stepping individual tabs, so they cannot reliably reach a requested display index.
/// </summary>
public sealed class StashTabSwitcher
{
    public enum Result { InProgress, Succeeded, Failed }

    private const int MaxClickAttempts = 4;
    private const int UniqueLabelFallbackMs = 1200;
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
        Status = $"locating tab '{_targetName}'";
    }

    public void Reset()
    {
        _targetName = string.Empty;
        _clickAttempts = 0;
        _hoverCandidateIndex = 0;
        _hoverStartedAt = TimeSpan.MinValue;
        _hoverTarget = 0;
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

        var current = ctx.Snapshot.StashInventory.VisibleTabIndex;
        if (catalog.FindSelected(_targetName, _requireGeneralPurpose, current) is { } selected)
        {
            Status = $"on tab '{selected.Name}' index={selected.DisplayIndex}";
            BubblesBot.Bot.Diagnostics.EventLog.Emit(
                "stash", "stash.tab-selected",
                BubblesBot.Bot.Diagnostics.EventSeverity.Info,
                Status,
                new Dictionary<string, object?>
                {
                    ["name"] = target.Name,
                    ["displayIndex"] = selected.DisplayIndex,
                    ["type"] = selected.Type,
                    ["generalPurposeRequired"] = _requireGeneralPurpose,
                });
            return Result.Succeeded;
        }
        if (current < 0)
        {
            Status = "waiting for visible stash index";
            return Result.InProgress;
        }
        var maxClickAttempts = LatencyPolicy.RetryLimit(MaxClickAttempts, ctx.Settings);
        if (_clickAttempts >= maxClickAttempts)
            return Fail($"click limit selecting '{target.Name}' from index {current}");
        if (BotMonotonicClock.ElapsedSince(_lastActionAt).TotalMilliseconds < 250)
            return Result.InProgress;

        var candidates = ctx.Snapshot.StashTabStrip.FindExact(target.Name)
            .Where(candidate => candidate.Rect.CenterX >= 0
                && candidate.Rect.CenterX < ctx.Snapshot.Window.Width
                && candidate.Rect.CenterY >= 0
                && candidate.Rect.CenterY < ctx.Snapshot.Window.Height)
            .ToArray();
        if (candidates.Length == 0)
            return Fail($"exact visible stash tab label '{target.Name}' not found");

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

        var ticket = ctx.Input.Click(
            sx, sy, ClickIntent.InteractUi,
            $"select exact stash tab '{target.Name}'",
            expectResolved: () => _getSnapshot() is { } live
                && live.IsStashOpen
                && live.StashTabs.FindSelected(
                    _targetName, _requireGeneralPurpose,
                    live.StashInventory.VisibleTabIndex) is not null,
            timeoutMs: 1800);
        if (ticket.Accepted)
        {
            _clickAttempts++;
            _lastActionAt = BotMonotonicClock.Now;
            _hoverCandidateIndex = (_hoverCandidateIndex + 1) % candidates.Length;
            _hoverTarget = 0;
            _hoverStartedAt = TimeSpan.MinValue;
            Status = $"selecting '{target.Name}' from index {current} ({_clickAttempts}/{maxClickAttempts})";
        }
        return Result.InProgress;
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
