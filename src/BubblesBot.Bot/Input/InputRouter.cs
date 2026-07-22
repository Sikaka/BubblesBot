using BubblesBot.Bot.Diagnostics;
using BubblesBot.Bot.Settings;
using BubblesBot.Bot.Systems;

namespace BubblesBot.Bot.Input;

/// <summary>
/// Default <see cref="IInputRouter"/>. One global click gate, per-vk held-key tracking, and an
/// unthrottled cursor-only hover path.
///
/// <para><b>Held keys.</b> A held key has at most one live handle at a time. If a behavior asks
/// for a hold on a key that's already held by a live handle, we return the existing handle and
/// refresh its TTL. The key-down event is sent once on first acquire; the key-up event is sent
/// when the handle is released, the TTL elapses without a refresh, the area changes, or the
/// foreground is lost. The TTL is the safety net — even if a behavior crashes mid-tick the key
/// is released within ~250 ms.</para>
///
/// <para><b>Click gate.</b> One click in flight, post-action settle floor, predicate-or-timeout
/// resolution. Held keys do NOT pass through the gate; they coexist with whatever click is in
/// flight (you can hold-cast while waiting on a loot click to confirm). A click with no
/// <c>expectResolved</c> predicate cannot be verified, so its token is capped at a short
/// timeout (see <see cref="UnverifiedTimeoutMs"/>) rather than holding the gate for the full
/// default — otherwise an unverifiable click would stall all clicks for 1.5 s.</para>
///
/// <para><b>Hover.</b> Cursor-only moves are NOT throttled — <c>SetCursorPos</c> is
/// microsecond-cheap and combat legitimately redirects the cursor several times per tick.
/// Multiple <c>HoverAt</c> calls per tick are normal.</para>
///
/// <para><b>Threading.</b> Strict single-thread: everything runs on the render/tick loop.
/// <see cref="Tick"/> must be called each frame (TTL math is monotonic). No internal locking.</para>
/// </summary>
public sealed class InputRouter : IInputRouter
{
    private readonly Func<int> _getLatencyAllowanceMs;

    public InputRouter(Func<int>? getLatencyAllowanceMs = null)
        => _getLatencyAllowanceMs = getLatencyAllowanceMs ?? (() => 0);

    /// <summary>
    /// Hard floor between consecutive click dispatches. PoE needs a moment to register the
    /// click before a follow-up can land cleanly. Held keys ignore this floor — they're
    /// stateful, not edge-triggered.
    /// </summary>
    private const int PostActionSettleMs = 100;

    /// <summary>
    /// Cap for a click issued without an <c>expectResolved</c> predicate. Such a click can only
    /// resolve by timeout (there's nothing to confirm), so we resolve it fast instead of holding
    /// the gate for the full default 1500 ms. Callers that need the gate held until a real
    /// post-condition must supply a predicate.
    /// </summary>
    private const int UnverifiedTimeoutMs = 300;

    private ActionToken? _pending;
    private PendingPointerAction? _pendingPointer;
    private TimeSpan _settleUntil = TimeSpan.MinValue;
    private long _nextActionId;
    private bool _hasHover;
    private int _hoverX, _hoverY;
    private CursorPriority _hoverPriority;

    private readonly Dictionary<int, HeldKey> _held = new();
    private readonly Dictionary<int, TimeSpan> _lastHoldReleaseAt = new();

    /// <summary>
    /// Safety floor between a key-up and the next key-down for the same held key. A held
    /// movement stream should not normally restart at all; if behavior composition regresses,
    /// this limits the edge rate to four restarts/second instead of allowing a server kick.
    /// </summary>
    private static readonly TimeSpan HoldRestartFloor = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Aggregate edge-action budget (key taps + clicks) over a sliding window. PoE's server
    /// disconnects clients that push too many actions per second ("too many actions" kick,
    /// observed live 2026-07-14 with a misconfigured profile); this cap makes that class of
    /// config mistake impossible to escalate into a kick. Held keys are exempt — a held
    /// move key is one continuous action stream, not repeated edges.
    /// </summary>
    private const int ActionWindowMs = 1000;
    private const int MaxActionsPerWindow = 6;
    private readonly Queue<TimeSpan> _recentActions = new();

    private bool TryConsumeActionBudget()
    {
        var now = BotMonotonicClock.Now;
        while (_recentActions.Count > 0 && (now - _recentActions.Peek()).TotalMilliseconds > ActionWindowMs)
            _recentActions.Dequeue();
        if (_recentActions.Count >= MaxActionsPerWindow) return false;
        _recentActions.Enqueue(now);
        return true;
    }

    public bool IsIdle => _pending is null && BotMonotonicClock.Now >= _settleUntil;

    public string GateState
    {
        get
        {
            if (_pending is { IsResolved: false } p)
                return $"waiting: {p.Description}";
            if (BotMonotonicClock.Now < _settleUntil) return "settling";
            return _held.Count > 0 ? $"idle (holding {_held.Count})" : "idle";
        }
    }

    public IInputHandle BeginHoldKey(int vk, HoldBudget budget)
    {
        // Coalesce repeat acquires onto the existing handle. Sending a fresh KEYDOWN while the
        // key is already down would generate auto-repeat events — fine for chat, bad for game
        // skills that listen for the down/up edge.
        if (_held.TryGetValue(vk, out var existing) && existing.IsActive)
        {
            existing.Refresh();
            return existing;
        }

        var now = BotMonotonicClock.Now;
        if (_lastHoldReleaseAt.TryGetValue(vk, out var releasedAt)
            && now - releasedAt < HoldRestartFloor)
        {
            EventLog.Emit("input", "input.hold-restart-suppressed", EventSeverity.Warning,
                $"hold restart suppressed vk=0x{vk:X2}", new Dictionary<string, object?>
                {
                    ["vk"] = vk,
                    ["sinceReleaseMs"] = (now - releasedAt).TotalMilliseconds,
                    ["floorMs"] = HoldRestartFloor.TotalMilliseconds,
                });
            return SuppressedHold.Instance;
        }

        SendInputNative.KeyDown(vk);
        Diagnostics.EventLog.Log("input", $"hold acquire vk=0x{vk:X2}");
        var handle = new HeldKey(this, vk, budget);
        _held[vk] = handle;
        return handle;
    }

    public void HoverAt(int absX, int absY, CursorPriority priority = CursorPriority.Walk)
    {
        // No rate limit — cursor moves are microsecond-cheap via SetCursorPos and combat
        // needs to redirect cursor onto an enemy briefly even after movement just hovered
        // toward a walk target. Multiple HoverAts per tick is normal usage now.
        if (_pendingPointer is not null) return;
        if (_hasHover && priority < _hoverPriority) return;
        _hasHover = true;
        _hoverX = absX;
        _hoverY = absY;
        _hoverPriority = priority;
    }

    /// <summary>
    /// Cursor-settle window between MoveCursor and the click event. Per the interaction
    /// golden rule: PoE's UI hover-detection needs the cursor to settle on a button before
    /// a click registers correctly. Without this, clicks on between-wave accept-trial
    /// buttons / ground labels / inventory items frequently "miss" — cursor moves to the
    /// right pixel but the click fires before the game registers the hover and rejects the
    /// click as not-on-element. 20-50ms is the documented working range; we randomize in
    /// that band to also serve as human-input jitter.
    /// </summary>
    private static int CursorSettleMs() => 25 + Random.Shared.Next(15);   // 25-39ms

    public InputTicket Click(int absX, int absY, ClickIntent intent, string description,
        Func<bool>? expectResolved = null, int timeoutMs = 1500)
    {
        var actionId = Request(intent, description, "left-click", absX, absY);
        if (!IsIdle) return Suppress(actionId, intent, description, "gate-busy");
        if (!TryConsumeActionBudget()) return Suppress(actionId, intent, description, "action-budget");
        Accepted(actionId, intent, description);

        SendInputNative.MoveCursor(absX, absY);
        return QueuePointerAction(actionId, intent, $"{intent}: {description}",
            PointerActionKind.LeftClick, null, expectResolved, timeoutMs);
    }

    public InputTicket RightClick(int absX, int absY, ClickIntent intent, string description,
        Func<bool>? expectResolved = null, int timeoutMs = 1500)
    {
        var actionId = Request(intent, description, "right-click", absX, absY);
        if (!IsIdle) return Suppress(actionId, intent, description, "gate-busy");
        if (!TryConsumeActionBudget()) return Suppress(actionId, intent, description, "action-budget");
        Accepted(actionId, intent, description);
        SendInputNative.MoveCursor(absX, absY);
        // Right-click via the keyboard-tap path on VK_RBUTTON (0x02) — SendInputNative routes
        // that through MOUSEINPUT events, same as TapKey of any mouse VK.
        return QueuePointerAction(actionId, intent, $"{intent}: {description} (RMB)",
            PointerActionKind.RightClick, null, expectResolved, timeoutMs);
    }

    public InputTicket ModifierClick(int absX, int absY, int[] modifiers, ClickIntent intent, string description,
        Func<bool>? expectResolved = null, int timeoutMs = 1500)
    {
        if (modifiers is null || modifiers.Length == 0)
            return Click(absX, absY, intent, description, expectResolved, timeoutMs);
        var actionId = Request(intent, description, "modifier-click", absX, absY);
        if (!IsIdle) return Suppress(actionId, intent, description, "gate-busy");
        if (!TryConsumeActionBudget()) return Suppress(actionId, intent, description, "action-budget");
        Accepted(actionId, intent, description);

        SendInputNative.MoveCursor(absX, absY);
        // Hold each modifier down → click → release modifiers in reverse order. Done as a
        // synchronous sequence so the click happens with all modifiers latched.
        var modDesc = string.Join(",", modifiers.Select(m => $"0x{m:X}"));
        return QueuePointerAction(actionId, intent, $"{intent}: {description} (mod={modDesc})",
            PointerActionKind.ModifierLeftClick, modifiers.ToArray(), expectResolved, timeoutMs);
    }

    public InputTicket Drag(int startAbsX, int startAbsY, int endAbsX, int endAbsY,
        ClickIntent intent, string description,
        Func<bool>? expectResolved = null, int timeoutMs = 1500)
    {
        var actionId = Request(intent, description, "left-drag", startAbsX, startAbsY);
        if (!IsIdle) return Suppress(actionId, intent, description, "gate-busy");
        if (!TryConsumeActionBudget()) return Suppress(actionId, intent, description, "action-budget");
        Accepted(actionId, intent, description);
        SendInputNative.MoveCursor(startAbsX, startAbsY);
        return QueuePointerAction(actionId, intent, $"{intent}: {description}",
            PointerActionKind.LeftDrag, null, expectResolved, timeoutMs, endAbsX, endAbsY);
    }

    public InputTicket TapKey(int vk, ClickIntent intent, string description)
    {
        // TapKey is NOT gated by the click latch — combat skill taps are fire-and-forget,
        // not verified, and need to fire while the bot is actively holding the walk key
        // (which would block on IsIdle if we gated). The click gate only protects clicks
        // because clicks need post-condition verification (label disappeared, panel opened).
        // For verification semantics on a key tap, the caller layers a Cooldown / SkillBook
        // gate on top.

        // Sanity: don't tap a key that's also being held — the up edge would clobber the hold.
        var actionId = Request(intent, description, "key-tap", vk: vk);
        if (_held.TryGetValue(vk, out var h) && h.IsActive)
            return Suppress(actionId, intent, description, "key-held");

        // Server-kick insurance: taps share the aggregate edge-action budget. A refused tap
        // simply fires on a later tick — every caller already treats unaccepted tickets as
        // "retry next tick".
        if (!TryConsumeActionBudget()) return Suppress(actionId, intent, description, "action-budget");

        Accepted(actionId, intent, description);
        SendInputNative.KeyTap(vk);

        // Returning Accepted=true with no token. Callers that want post-condition tracking
        // (rare for skill taps) can layer their own polling — but defaulting to "no token"
        // means TapKey doesn't fight the click gate state machine.
        var wallNow = DateTime.UtcNow;
        var now = BotMonotonicClock.Now;
        var token = new ActionToken(actionId, description, wallNow, 1, null, now);
        token.ConfirmImmediately(wallNow, now);
        EmitAction(token, intent, "dispatched", EventSeverity.Info);
        EmitAction(token, intent, "confirmed", EventSeverity.Info);
        return new InputTicket(token);
    }

    public InputTicket VerifiedTapKey(int vk, ClickIntent intent, string description,
        Func<bool> expectResolved, int timeoutMs = 1500)
    {
        var actionId = Request(intent, description, "verified-key-tap", vk: vk);
        if (!IsIdle) return Suppress(actionId, intent, description, "gate-busy");
        if (_held.TryGetValue(vk, out var h) && h.IsActive)
            return Suppress(actionId, intent, description, "key-held");
        if (!TryConsumeActionBudget()) return Suppress(actionId, intent, description, "action-budget");
        Accepted(actionId, intent, description);
        SendInputNative.KeyTap(vk);
        return DispatchToken(actionId, intent, $"{intent}: {description}", expectResolved, timeoutMs);
    }

    public InputTicket VerifiedTapScanCode(int scanCode, ClickIntent intent, string description,
        Func<bool> expectResolved, int timeoutMs = 1500)
    {
        var actionId = Request(intent, description, "verified-scan-code-tap", vk: scanCode);
        if (!IsIdle) return Suppress(actionId, intent, description, "gate-busy");
        if (!TryConsumeActionBudget()) return Suppress(actionId, intent, description, "action-budget");
        Accepted(actionId, intent, description);
        SendInputNative.ScanCodeTap(scanCode);
        return DispatchToken(actionId, intent, $"{intent}: {description}", expectResolved, timeoutMs);
    }

    private InputTicket DispatchToken(long actionId, ClickIntent intent, string description,
        Func<bool>? expectResolved, int timeoutMs)
    {
        var wallNow = DateTime.UtcNow;
        var now = BotMonotonicClock.Now;
        _settleUntil = now + TimeSpan.FromMilliseconds(PostActionSettleMs);
        // A predicate-less click can't be verified — cap its lifetime so it doesn't wedge the
        // gate for the full 1.5 s default. With a predicate, honor the caller's timeout.
        var effectiveTimeout = EffectiveTimeout(timeoutMs, expectResolved is not null);
        _pending = new ActionToken(actionId, description, wallNow, effectiveTimeout, expectResolved, now);
        _pending.MarkDispatched(wallNow, now);
        EmitAction(_pending, intent, "dispatched", EventSeverity.Info);
        return new InputTicket(_pending);
    }

    private InputTicket QueuePointerAction(long actionId, ClickIntent intent, string description,
        PointerActionKind kind, int[]? modifiers, Func<bool>? expectResolved, int timeoutMs,
        int endX = 0, int endY = 0)
    {
        var wallNow = DateTime.UtcNow;
        var now = BotMonotonicClock.Now;
        var effectiveTimeout = EffectiveTimeout(timeoutMs, expectResolved is not null);
        _pending = new ActionToken(actionId, description, wallNow, effectiveTimeout, expectResolved, now);
        _pendingPointer = new PendingPointerAction(
            _pending, intent, kind, modifiers, now + TimeSpan.FromMilliseconds(CursorSettleMs()),
            endX, endY);
        return new InputTicket(_pending);
    }

    private int EffectiveTimeout(int requestedTimeoutMs, bool verified)
        => verified
            ? LatencyPolicy.TimeoutMs(requestedTimeoutMs, _getLatencyAllowanceMs())
            : Math.Min(requestedTimeoutMs, UnverifiedTimeoutMs);

    public void Tick()
    {
        var wallNow = DateTime.UtcNow;
        var now = BotMonotonicClock.Now;

        if (_pendingPointer is null && _hasHover)
            SendInputNative.MoveCursor(_hoverX, _hoverY);
        _hasHover = false;
        _hoverPriority = CursorPriority.Walk;

        if (_pendingPointer is { } pointer && now >= pointer.DispatchAt)
        {
            switch (pointer.Kind)
            {
                case PointerActionKind.LeftClick:
                    SendInputNative.LeftClick();
                    break;
                case PointerActionKind.RightClick:
                    SendInputNative.KeyTap(0x02);
                    break;
                case PointerActionKind.ModifierLeftClick:
                    var modifiers = pointer.Modifiers ?? [];
                    foreach (var vk in modifiers) SendInputNative.KeyDown(vk);
                    try { SendInputNative.LeftClick(); }
                    finally
                    {
                        for (var i = modifiers.Length - 1; i >= 0; i--)
                            SendInputNative.KeyUp(modifiers[i]);
                    }
                    break;
                case PointerActionKind.LeftDrag:
                    SendInputNative.LeftDrag(pointer.EndX, pointer.EndY);
                    break;
            }
            pointer.Token.MarkDispatched(wallNow, now);
            _settleUntil = now + TimeSpan.FromMilliseconds(PostActionSettleMs);
            EmitAction(pointer.Token, pointer.Intent, "dispatched", EventSeverity.Info);
            _pendingPointer = null;
        }

        if (_pending is not null && _pending.Poll(wallNow, now))
        {
            var resolved = _pending;
            var stage = resolved.Outcome switch
            {
                ActionOutcome.Confirmed => "confirmed",
                ActionOutcome.SettledUnverified => "settled-unverified",
                ActionOutcome.TimedOut => "timed-out",
                ActionOutcome.Cancelled => "cancelled",
                _ => "resolved",
            };
            EmitAction(resolved, null, stage,
                resolved.Outcome == ActionOutcome.TimedOut ? EventSeverity.Warning : EventSeverity.Info);
            _pending = null;
        }

        // Age-out held keys whose TTL elapsed without a refresh. Snapshot first so the
        // Release()'s dictionary mutation doesn't invalidate the enumerator.
        if (_held.Count > 0)
        {
            List<HeldKey>? expired = null;
            foreach (var kv in _held)
                if (kv.Value.IsExpired(now))
                    (expired ??= new List<HeldKey>()).Add(kv.Value);
            if (expired is not null)
                foreach (var h in expired) h.Release(now >= h.MaxAt ? "max-duration" : "idle-ttl");
        }
    }

    public void CancelAll()
    {
        if (_pending is { } pending)
        {
            pending.Cancel(DateTime.UtcNow, BotMonotonicClock.Now);
            EmitAction(pending, null, "cancelled", EventSeverity.Warning);
        }
        _pending = null;
        _pendingPointer = null;
        _hasHover = false;
        _settleUntil = TimeSpan.MinValue;
        ReleaseAllHeld();
    }

    public void OnAreaChanged() => CancelAll();
    public void OnForegroundLost() => CancelAll();

    private void ReleaseAllHeld()
    {
        if (_held.Count == 0) return;
        var snapshot = new List<HeldKey>(_held.Values);
        foreach (var h in snapshot) h.Release();
    }

    /// <summary>Called by <see cref="HeldKey.Release"/> to drop itself from the dict.</summary>
    private void OnHeldReleased(int vk, HeldKey handle)
    {
        if (_held.TryGetValue(vk, out var current) && ReferenceEquals(current, handle))
        {
            _held.Remove(vk);
            _lastHoldReleaseAt[vk] = BotMonotonicClock.Now;
        }
    }

    private long Request(ClickIntent intent, string description, string actionKind,
        int? x = null, int? y = null, int? vk = null)
    {
        var id = ++_nextActionId;
        EventLog.Emit("input", "input.requested", EventSeverity.Debug,
            description, new Dictionary<string, object?>
            {
                ["actionId"] = id,
                ["intent"] = intent.ToString(),
                ["actionKind"] = actionKind,
                ["x"] = x,
                ["y"] = y,
                ["vk"] = vk,
            });
        return id;
    }

    private static void Accepted(long actionId, ClickIntent intent, string description)
        => EventLog.Emit("input", "input.accepted", EventSeverity.Debug,
            description, new Dictionary<string, object?>
            {
                ["actionId"] = actionId,
                ["intent"] = intent.ToString(),
            });

    private static InputTicket Suppress(long actionId, ClickIntent intent, string description, string reason)
    {
        EventLog.Emit("input", "input.suppressed", EventSeverity.Debug,
            description, new Dictionary<string, object?>
            {
                ["actionId"] = actionId,
                ["intent"] = intent.ToString(),
                ["reason"] = reason,
            });
        return new InputTicket(null);
    }

    private static void EmitAction(ActionToken token, ClickIntent? intent, string stage, EventSeverity severity)
        => EventLog.Emit("input", $"input.{stage}", severity,
            token.Description, new Dictionary<string, object?>
            {
                ["actionId"] = token.ActionId,
                ["intent"] = intent?.ToString(),
                ["outcome"] = token.Outcome.ToString(),
                ["timeoutMs"] = token.TimeoutMs,
                ["latencyMs"] = token.DispatchedMonotonic is { } sent && token.ResolvedMonotonic is { } resolved
                    ? (resolved - sent).TotalMilliseconds
                    : null,
            });

    private enum PointerActionKind { LeftClick, RightClick, ModifierLeftClick, LeftDrag }
    private sealed record PendingPointerAction(
        ActionToken Token,
        ClickIntent Intent,
        PointerActionKind Kind,
        int[]? Modifiers,
        TimeSpan DispatchAt,
        int EndX,
        int EndY);

    private sealed class HeldKey : IInputHandle
    {
        private readonly InputRouter _owner;
        private readonly int _vk;
        private readonly TimeSpan _maxAt;
        private readonly TimeSpan _idleTtl;
        private readonly TimeSpan _acquiredAt;
        private TimeSpan _lastRefreshAt;
        private bool _released;

        public HeldKey(InputRouter owner, int vk, HoldBudget budget)
        {
            _owner = owner;
            _vk = vk;
            _idleTtl = budget.ReleaseAfterIdle;
            var now = BotMonotonicClock.Now;
            _acquiredAt = now;
            _maxAt = now + budget.MaxDuration;
            _lastRefreshAt = now;
        }

        public TimeSpan MaxAt => _maxAt;

        public bool IsActive => !_released;

        public void Refresh() { if (!_released) _lastRefreshAt = BotMonotonicClock.Now; }

        public bool IsExpired(TimeSpan now)
            => !_released && (now >= _maxAt || (now - _lastRefreshAt) >= _idleTtl);

        public void Release() => Release("explicit");

        /// <summary>
        /// Release with a cause label. Every keyup is logged: hold-release/re-acquire
        /// cycling turns the "one held move key" into a stream of discrete keypresses —
        /// the leading suspect for the 2026-07-14 "too many actions" server kick — and
        /// the release reasons in the event log are how we measure that rate in the field.
        /// </summary>
        public void Release(string reason)
        {
            if (_released) return;
            _released = true;
            try   { SendInputNative.KeyUp(_vk); }
            finally
            {
                _owner.OnHeldReleased(_vk, this);
                Diagnostics.EventLog.Log("input",
                    $"hold release vk=0x{_vk:X2} after {(BotMonotonicClock.Now - _acquiredAt).TotalMilliseconds:F0}ms ({reason})");
            }
        }

        public void Dispose() => Release("dispose");
    }

    private sealed class SuppressedHold : IInputHandle
    {
        public static readonly SuppressedHold Instance = new();
        public bool IsActive => false;
        public void Refresh() { }
        public void Release() { }
        public void Dispose() { }
    }
}
