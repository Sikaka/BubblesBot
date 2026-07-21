using BubblesBot.Bot.Behaviors;
using BubblesBot.Bot.Input;
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
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly Func<GameSnapshot?> _getSnapshot;
    private string _targetName = string.Empty;
    private bool _requireGeneralPurpose;
    private TimeSpan _startedAt;
    private TimeSpan _lastActionAt = TimeSpan.MinValue;
    private int _clickAttempts;
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
        _hoverTarget = 0;
        Status = $"locating tab '{_targetName}'";
    }

    public void Reset()
    {
        _targetName = string.Empty;
        _clickAttempts = 0;
        _hoverTarget = 0;
        Status = "idle";
    }

    public Result Tick(BehaviorContext ctx)
    {
        if (!ctx.Snapshot.IsStashOpen)
            return Fail("stash is closed");
        if (_targetName.Length == 0)
            return Fail("target tab is empty");
        if (BotMonotonicClock.ElapsedSince(_startedAt) > Timeout)
            return Fail($"timeout switching to '{_targetName}'");

        var catalog = ctx.Snapshot.StashTabs;
        var target = catalog.Find(_targetName, _requireGeneralPurpose);
        if (target is null)
            return Fail(_requireGeneralPurpose
                ? $"general-purpose stash tab '{_targetName}' not found"
                : $"stash tab '{_targetName}' not found");

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
        if (_clickAttempts >= MaxClickAttempts)
            return Fail($"click limit selecting '{target.Name}' from index {current}");
        if (BotMonotonicClock.ElapsedSince(_lastActionAt).TotalMilliseconds < 250)
            return Result.InProgress;

        var candidates = ctx.Snapshot.StashTabStrip.FindExact(target.Name);
        if (candidates.Count == 0)
            return Fail($"exact visible stash tab label '{target.Name}' not found");

        // Duplicate tab names are common after migrations. Try each exact visible label in
        // deterministic left-to-right order; only the server-authoritative display index can
        // confirm that the requested duplicate was selected.
        var candidate = candidates[Math.Min(_clickAttempts, candidates.Count - 1)];
        var rect = candidate.Rect;
        var (sx, sy) = ctx.Snapshot.Window.ToScreen((int)rect.CenterX, (int)rect.CenterY);
        if (_hoverTarget != candidate.Element || !HoverResolvesTo(ctx.Snapshot, candidate.Element))
        {
            _hoverTarget = candidate.Element;
            ctx.Input.HoverAt(sx, sy, CursorPriority.Halt);
            Status = $"verifying exact tab label '{target.Name}'";
            return Result.InProgress;
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
            _hoverTarget = 0;
            Status = $"selecting '{target.Name}' from index {current} ({_clickAttempts}/{MaxClickAttempts})";
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
