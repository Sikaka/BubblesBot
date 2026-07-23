using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Input;

/// <summary>
/// Why we're moving the cursor. Held movement implies a re-target loop; clicks are one-shot.
/// Used today only for diagnostics in the gate-state string but kept as a typed enum so future
/// behaviors (combat-aim, ground-target) read clearly at the call site.
/// </summary>
public enum MoveIntent
{
    Walk,
    BlinkOrDash,
    AimSkill,
    AimGroundTarget,
}

public enum CursorPriority
{
    Walk = 10,
    Halt = 20,
    CombatAim = 30,
    BlinkAim = 40,
}

/// <summary>What the click is meant to accomplish. Surfaces in the gate description.</summary>
public enum ClickIntent
{
    LootGround,
    InteractWorld,
    InteractUi,
    UseSkill,
}

/// <summary>
/// Caps on a held key. <see cref="MaxDuration"/> is a hard ceiling (defensive — a bug in the
/// behavior loop must not be able to lock the key down forever). <see cref="ReleaseAfterIdle"/>
/// is a soft TTL: if nothing refreshes the handle inside this window the router releases it.
/// Behaviors are expected to call <see cref="IInputHandle.Refresh"/> each tick they still
/// want the key down.
/// </summary>
public readonly record struct HoldBudget(TimeSpan MaxDuration, TimeSpan ReleaseAfterIdle)
{
    public static HoldBudget Default => new(TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(250));
}

/// <summary>
/// Receipt for a queued click. Today the router has no queue (one-in-flight gate), so the
/// ticket just exposes the underlying token. Kept as its own type so the public surface
/// doesn't change when we add queueing.
/// </summary>
public readonly record struct InputTicket(ActionToken? Token)
{
    public bool   Accepted    => Token is not null;
    public bool   IsResolved  => Token is null || Token.IsResolved;
    public string Description => Token?.Description ?? "(suppressed)";
}

/// <summary>
/// Live handle to a held input — keyboard hold today, will grow to mouse-button hold for
/// hold-to-walk in step 2. Behaviors must call <see cref="Refresh"/> each tick they still
/// want it active; the router auto-releases on TTL, area change, foreground loss, or
/// <see cref="IDisposable.Dispose"/>. Calling <see cref="Release"/> is the same as Dispose.
/// </summary>
public interface IInputHandle : IDisposable
{
    bool IsActive { get; }

    /// <summary>Mark this handle as still wanted. Resets the idle TTL.</summary>
    void Refresh();

    /// <summary>Explicit release. Idempotent. Same as Dispose.</summary>
    void Release();
}

/// <summary>
/// Single front door for game input. All bot behaviors go through this — never call
/// <see cref="SendInputNative"/> directly. Centralizes the gate (one click in flight),
/// held-key tracking, and the kill-switch surface (<see cref="OnAreaChanged"/>,
/// <see cref="OnForegroundLost"/>, <see cref="CancelAll"/>).
/// </summary>
public interface IInputRouter
{
    /// <summary>
    /// Begin or refresh a hold on a Win32 virtual-key. If the key is already held by a live
    /// handle the existing handle is returned (and refreshed); otherwise a key-down is sent
    /// and a new handle is issued. Bypasses the click gate.
    /// </summary>
    IInputHandle BeginHoldKey(int vk, HoldBudget budget);

    /// <summary>
    /// Move the cursor without clicking. No gate and NOT throttled — <c>SetCursorPos</c> is
    /// microsecond-cheap and callers legitimately hover several times per tick (walk-aim then
    /// skill-aim). Used for the "park cursor on player to halt movement" trick and for skill
    /// aiming. When several callers want the cursor in one tick, route them through
    /// <c>CursorArbiter</c> so exactly one wins by priority.
    /// </summary>
    void HoverAt(int absX, int absY, CursorPriority priority = CursorPriority.Walk);

    /// <summary>
    /// Move cursor + left-click at absolute screen coords, gated through the global one-in-
    /// flight latch. Returns a ticket whose <see cref="InputTicket.Accepted"/> is false when
    /// the gate suppressed the call.
    /// </summary>
    InputTicket Click(int absX, int absY, ClickIntent intent, string description,
        Func<bool>? expectResolved = null, int timeoutMs = 1500);

    /// <summary>
    /// Tap a Win32 virtual-key (down + up). NOT gated — fire-and-forget so skill taps fire
    /// while the walk key is held and while a click is in flight. Suppressed only if that VK
    /// is currently held (the up-edge would clobber the hold). No per-VK cooldown here; callers
    /// layer a <c>Cooldown</c>/<c>SkillBook</c> gate for spacing. Used for hotbar casts,
    /// Escape, inventory toggle, flasks, and blinks.
    /// </summary>
    InputTicket TapKey(int vk, ClickIntent intent, string description);

    /// <summary>Tap a key through the one-in-flight gate and verify a memory postcondition.</summary>
    InputTicket VerifiedTapKey(int vk, ClickIntent intent, string description,
        Func<bool> expectResolved, int timeoutMs = 1500);

    /// <summary>
    /// Tap a key with modifier keys latched (e.g. Ctrl+Left to step the active stash tab), through
    /// the one-in-flight gate with a verified postcondition. Modifiers are pressed before the tap
    /// and released after, in reverse order.
    /// </summary>
    InputTicket VerifiedModifierTapKey(int vk, int[] modifiers, ClickIntent intent, string description,
        Func<bool> expectResolved, int timeoutMs = 1500);

    /// <summary>Tap a hardware scan code through the one-in-flight gate and verify a postcondition.</summary>
    InputTicket VerifiedTapScanCode(int scanCode, ClickIntent intent, string description,
        Func<bool> expectResolved, int timeoutMs = 1500);

    /// <summary>
    /// Move cursor + right-click at absolute screen coords. Used for "send map to device"
    /// (right-click on inventory map while device is open), inventory item context menus,
    /// etc. Gated like <see cref="Click"/>.
    /// </summary>
    InputTicket RightClick(int absX, int absY, ClickIntent intent, string description,
        Func<bool>? expectResolved = null, int timeoutMs = 1500);

    /// <summary>
    /// Move cursor + left-click at absolute screen coords with held modifier keys (e.g.
    /// <c>VK_LCONTROL = 0xA2</c> for Ctrl+click, <c>VK_LSHIFT = 0xA0</c> for Shift+click).
    /// Modifiers are held down before the click and released after. Used for "Ctrl+click
    /// to insert map into device" and similar item-routing actions.
    /// </summary>
    InputTicket ModifierClick(int absX, int absY, int[] modifiers, ClickIntent intent, string description,
        Func<bool>? expectResolved = null, int timeoutMs = 1500);

    /// <summary>
    /// Hold the left mouse button at one point, move to another, and release. Used for
    /// panning draggable UI canvases such as the Atlas; gated and postcondition-verified like
    /// other pointer mutations.
    /// </summary>
    InputTicket Drag(int startAbsX, int startAbsY, int endAbsX, int endAbsY,
        ClickIntent intent, string description,
        Func<bool>? expectResolved = null, int timeoutMs = 1500);

    /// <summary>True when no click is in flight and the post-action settle has elapsed.</summary>
    bool IsIdle { get; }

    /// <summary>Short human-readable string for the status panel: "idle" / "settling" / "waiting: …".</summary>
    string GateState { get; }

    /// <summary>
    /// Drive in-flight tokens + age-out held handles. Call once per render tick — held-handle
    /// TTL math runs against the current time, so this needs to fire even when no behavior
    /// is asking for input.
    /// </summary>
    void Tick();

    /// <summary>Drop the in-flight click and release every held handle. Use when toggling the bot off.</summary>
    void CancelAll();

    /// <summary>Area-change side effects: identical to <see cref="CancelAll"/> today, kept as its own entry point so behaviors can hook it.</summary>
    void OnAreaChanged();

    /// <summary>Foreground-loss side effects: release everything. PoE wouldn't receive the input anyway.</summary>
    void OnForegroundLost();
}
