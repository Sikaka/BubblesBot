using BubblesBot.Bot.Input;

namespace BubblesBot.Bot.LiveTests;

/// <summary>
/// Live-test-only guard around the production <see cref="InputRouter"/>. The inner router still
/// owns action budgets, click settling, postconditions, and held-key cleanup; this wrapper adds
/// per-dispatch character/area/window/game-state checks so a prepared test cannot continue after
/// its setup becomes invalid.
/// </summary>
internal sealed class GuardedLiveTestInput : IInputRouter
{
    private readonly InputRouter _inner;
    private readonly Func<(bool Allowed, string Reason)> _canDispatch;
    private readonly Action<string> _onBlocked;
    private string? _lastBlockedReason;

    public GuardedLiveTestInput(
        InputRouter inner,
        Func<(bool Allowed, string Reason)> canDispatch,
        Action<string> onBlocked)
    {
        _inner = inner;
        _canDispatch = canDispatch;
        _onBlocked = onBlocked;
    }

    public bool IsIdle => _inner.IsIdle;
    public string GateState => _inner.GateState;

    public IInputHandle BeginHoldKey(int vk, HoldBudget budget)
        => Allowed() ? _inner.BeginHoldKey(vk, budget) : NoopInputHandle.Instance;

    public void HoverAt(int absX, int absY, CursorPriority priority = CursorPriority.Walk)
    {
        if (Allowed()) _inner.HoverAt(absX, absY, priority);
    }

    public InputTicket Click(int absX, int absY, ClickIntent intent, string description,
        Func<bool>? expectResolved = null, int timeoutMs = 1500)
        => Allowed()
            ? _inner.Click(absX, absY, intent, description, expectResolved, timeoutMs)
            : new InputTicket(null);

    public InputTicket RightClick(int absX, int absY, ClickIntent intent, string description,
        Func<bool>? expectResolved = null, int timeoutMs = 1500)
        => Allowed()
            ? _inner.RightClick(absX, absY, intent, description, expectResolved, timeoutMs)
            : new InputTicket(null);

    public InputTicket ModifierClick(int absX, int absY, int[] modifiers, ClickIntent intent,
        string description, Func<bool>? expectResolved = null, int timeoutMs = 1500)
        => Allowed()
            ? _inner.ModifierClick(absX, absY, modifiers, intent, description, expectResolved, timeoutMs)
            : new InputTicket(null);

    public InputTicket Drag(int startAbsX, int startAbsY, int endAbsX, int endAbsY,
        ClickIntent intent, string description,
        Func<bool>? expectResolved = null, int timeoutMs = 1500)
        => Allowed()
            ? _inner.Drag(startAbsX, startAbsY, endAbsX, endAbsY,
                intent, description, expectResolved, timeoutMs)
            : new InputTicket(null);

    public InputTicket TapKey(int vk, ClickIntent intent, string description)
        => Allowed() ? _inner.TapKey(vk, intent, description) : new InputTicket(null);

    public InputTicket VerifiedTapKey(int vk, ClickIntent intent, string description,
        Func<bool> expectResolved, int timeoutMs = 1500)
        => Allowed()
            ? _inner.VerifiedTapKey(vk, intent, description, expectResolved, timeoutMs)
            : new InputTicket(null);

    public InputTicket VerifiedTapScanCode(int scanCode, ClickIntent intent, string description,
        Func<bool> expectResolved, int timeoutMs = 1500)
        => Allowed()
            ? _inner.VerifiedTapScanCode(scanCode, intent, description, expectResolved, timeoutMs)
            : new InputTicket(null);

    public void Tick()
    {
        if (Allowed()) _inner.Tick();
        else _inner.CancelAll();
    }

    public void CancelAll() => _inner.CancelAll();
    public void OnAreaChanged() => _inner.OnAreaChanged();
    public void OnForegroundLost() => _inner.OnForegroundLost();

    private bool Allowed()
    {
        var (allowed, reason) = _canDispatch();
        if (allowed)
        {
            _lastBlockedReason = null;
            return true;
        }

        if (!string.Equals(_lastBlockedReason, reason, StringComparison.Ordinal))
        {
            _lastBlockedReason = reason;
            _onBlocked(reason);
        }
        return false;
    }

    private sealed class NoopInputHandle : IInputHandle
    {
        public static readonly NoopInputHandle Instance = new();
        public bool IsActive => false;
        public void Refresh() { }
        public void Release() { }
        public void Dispose() { }
    }
}
