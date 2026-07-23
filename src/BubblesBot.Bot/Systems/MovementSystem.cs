using BubblesBot.Bot.Input;
using BubblesBot.Bot.Settings;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Systems;

/// <summary>
/// Owns the held movement-skill key and the cursor-retarget loop. Movement in PoE is
/// "hold movement-skill key + cursor pointed where you want to go" — a held key, not a
/// click. So this system holds the configured move-skill VK via the input router and
/// re-points the cursor at the desired screen location each tick.
///
/// <para><b>Stop trick.</b> PoE stops the player when the movement target is at the
/// character's own position. To halt cleanly we hover the cursor onto the player's screen
/// position rather than releasing the key — which is faster than waiting for the held-key
/// TTL to expire and avoids a one-frame "no input" gap.</para>
///
/// <para><b>Off-screen targets.</b> When the requested grid cell projects off the screen,
/// we clamp to the nearest on-screen point along the ray from player to target. The held
/// key keeps the character moving roughly the right direction even when the target is far
/// outside the camera frustum.</para>
/// </summary>
public sealed class MovementSystem
{
    private readonly SettingsStore _settings;
    private IInputHandle? _holdHandle;
    private object? _holdOwner;
    private TimeSpan _lastWalkAt;

    public MovementSystem(SettingsStore settings)
    {
        _settings = settings;
    }

    /// <summary>True when the move-skill key is currently held.</summary>
    public bool IsHolding => _holdHandle is { IsActive: true };

    /// <summary>
    /// Hold the move-skill and point the cursor at <paramref name="targetGrid"/>. Call every
    /// tick the bot wants to walk to that target. Returns false when projection failed (no
    /// camera, no live player) — caller should treat as a transient block.
    /// </summary>
    /// <summary>
    /// Minimum aim distance (grid cells) from the player. Below this, the target cell projects
    /// almost onto the character and PoE reads "move target = self" — the same mechanic
    /// <see cref="Halt"/> uses to stop — so the held key produces no movement. When the next
    /// path cell is closer than this, we extend the cursor aim along the same heading so the
    /// character keeps moving in the right direction; the path's arrival check (6 cells) still
    /// advances the step as we close in. Fixes the "wedged ~7 cells from the next step" stall.
    /// </summary>
    private const float MinAimGrid = 12f;

    public bool WalkToward(Vector2i targetGrid, BehaviorContextLite ctx, object? owner = null)
    {
        var live = ctx.Live;
        if (live is null) return false;

        // Push a too-close target out to MinAimGrid along the player->target heading.
        var pg = live.Value.GridPosition;
        var dgx = (float)(targetGrid.X - pg.X);
        var dgy = (float)(targetGrid.Y - pg.Y);
        var gd = MathF.Sqrt(dgx * dgx + dgy * dgy);
        if (gd > 0.5f && gd < MinAimGrid)
        {
            var k = MinAimGrid / gd;
            targetGrid = new Vector2i { X = pg.X + (int)MathF.Round(dgx * k), Y = pg.Y + (int)MathF.Round(dgy * k) };
        }

        var screen = ctx.Snapshot.Camera.GridToScreenAtPlayerZ(targetGrid, live.Value.WorldPosition.Z);
        if (screen is null) return false;

        var (sx, sy) = screen.Value;
        // Clamp the cursor target into the window. PoE accepts off-window cursor positions
        // for held movement, but the OS may behave oddly across multi-monitor setups.
        var w = ctx.Snapshot.Window;
        sx = Math.Clamp(sx, 0, Math.Max(0, w.Width  - 1));
        sy = Math.Clamp(sy, 0, Math.Max(0, w.Height - 1));
        var (absX, absY) = w.ToScreen(sx, sy);

        // Ownership prevents an inactive behavior's Reset() from releasing a hold that a
        // different behavior acquired earlier in the same tree tick. This was observed live
        // in Blight: Selector.ResetAfter reset the later chest/exit FollowPaths after the
        // active sweep FollowPath had started walking, producing a key-down/key-up pair every
        // ~40 ms until the server kicked for too many actions.
        _holdOwner = owner;
        EnsureHold(ctx.Input);
        ctx.Input.HoverAt(absX, absY, CursorPriority.Walk);
        _lastWalkAt = BotMonotonicClock.Now;
        return true;
    }

    /// <summary>
    /// One discrete move-key TAP toward <paramref name="targetGrid"/> (any existing hold is released
    /// first). A tap toward an open heading moves the character a step; toward a blocker it doesn't —
    /// which the walk-only unstick uses to detect a blocked direction. Returns false if projection
    /// failed or no walk key is bound.
    /// </summary>
    public bool TapToward(Vector2i targetGrid, BehaviorContextLite ctx, object? owner = null)
    {
        Release(owner);                 // discrete tap, not a sustained hold
        var live = ctx.Live;
        if (live is null) return false;
        var walk = _settings.Current.Skills.WalkSlot;
        if (walk is null || walk.Vk == 0) return false;

        var screen = ctx.Snapshot.Camera.GridToScreenAtPlayerZ(targetGrid, live.Value.WorldPosition.Z);
        if (screen is null) return false;
        var (sx, sy) = screen.Value;
        var w = ctx.Snapshot.Window;
        sx = Math.Clamp(sx, 0, Math.Max(0, w.Width  - 1));
        sy = Math.Clamp(sy, 0, Math.Max(0, w.Height - 1));
        var (absX, absY) = w.ToScreen(sx, sy);

        ctx.Input.HoverAt(absX, absY, CursorPriority.Walk);   // SetCursorPos is synchronous
        ctx.Input.TapKey(walk.Vk, ClickIntent.UseSkill, "unstick nudge tap");
        return true;
    }

    /// <summary>
    /// Stop the player by hovering the cursor onto the player's own screen position. Cleaner
    /// than just releasing — PoE registers "movement target = self" as a stop immediately.
    /// </summary>
    public void Halt(BehaviorContextLite ctx, object? owner = null)
    {
        if (!CanRelease(_holdOwner, owner)) return;
        var live = ctx.Live;
        if (live is null) { Release(owner); return; }

        var selfScreen = ctx.Snapshot.Camera.WorldToScreen(live.Value.WorldPosition);
        if (selfScreen is null) { Release(owner); return; }

        var (sx, sy) = selfScreen.Value;
        var w = ctx.Snapshot.Window;
        sx = Math.Clamp(sx, 0, Math.Max(0, w.Width  - 1));
        sy = Math.Clamp(sy, 0, Math.Max(0, w.Height - 1));
        var (absX, absY) = w.ToScreen(sx, sy);

        // Park the cursor on self for a moment, then release the hold so the key isn't left
        // down indefinitely after we've stopped wanting to move.
        ctx.Input.HoverAt(absX, absY, CursorPriority.Halt);
        if ((BotMonotonicClock.Now - _lastWalkAt).TotalMilliseconds > 80) Release(owner);
    }

    /// <summary>
    /// Release the held move-skill key. An owner-scoped release is ignored when another
    /// behavior currently owns the hold; an ownerless release is an unconditional mode-level
    /// stop. Safe to call repeatedly.
    /// </summary>
    public void Release(object? owner = null)
    {
        if (!CanRelease(_holdOwner, owner)) return;
        _holdHandle?.Release();
        _holdHandle = null;
        _holdOwner = null;
    }

    public static bool CanRelease(object? currentOwner, object? requestedOwner)
        => requestedOwner is null || ReferenceEquals(currentOwner, requestedOwner);

    private void EnsureHold(IInputRouter input)
    {
        // The walk slot in the user's skill profile drives which key gets held. If they've
        // disabled all walk skills (or never bound one), there's nothing to hold and the
        // character won't move — the gate fails fast rather than holding a phantom key.
        var walk = _settings.Current.Skills.WalkSlot;
        if (walk is null || walk.Vk == 0) { _holdHandle?.Release(); _holdHandle = null; return; }
        if (_holdHandle is { IsActive: true }) { _holdHandle.Refresh(); return; }
        _holdHandle = input.BeginHoldKey(walk.Vk, HoldBudget.Default);
    }
}

/// <summary>
/// Subset of <see cref="Behaviors.BehaviorContext"/> that systems need. Avoids a project-cycle
/// dependency from Systems → Behaviors. Behaviors construct one of these from their context.
/// </summary>
public readonly record struct BehaviorContextLite(GameSnapshot Snapshot, IInputRouter Input, LivePlayer? Live);
