namespace BubblesBot.Bot.Systems;

using BubblesBot.Bot.Settings;

/// <summary>
/// Tracks the character's self-imposed action commitment — the "lock" after firing a skill that
/// roots you for a cast/attack animation. Attacks and stationary casts with a non-zero
/// <see cref="SkillSlot.LockMs"/> stamp a lock here when they fire; movement / dodge logic reads
/// <see cref="IsLocked"/> so it doesn't fight an in-progress animation (or so a dodge knows it
/// must actively break one).
///
/// <para><b>Backward-compatible by default.</b> Every skill ships <c>LockMs = 0</c>, so nothing
/// stamps a lock until a build opts in — existing drive-by tap-attack behavior is unchanged. A
/// <c>CombatSystem</c> with no <see cref="ActionState"/> wired records nothing at all.</para>
///
/// <para><b>Not authoritative.</b> This is a client-side estimate from configured
/// <see cref="SkillSlot.LockMs"/>. When <c>ActorComponent.AnimationController</c> reads are
/// validated, the real animation-progress can override this without changing the consumer API.
/// A dodge can call <see cref="Clear"/> to declare the lock broken (movement skills interrupt
/// most attack animations in PoE).</para>
/// </summary>
public sealed class ActionState
{
    private TimeSpan _lockedUntil;
    private string _lockSource = "";

    /// <summary>True while a fired action still commits the character (can't freely move).</summary>
    public bool IsLocked => BotMonotonicClock.Now < _lockedUntil;

    /// <summary>Milliseconds remaining on the current lock, 0 if unlocked.</summary>
    public int LockRemainingMs
    {
        get
        {
            var ms = (_lockedUntil - BotMonotonicClock.Now).TotalMilliseconds;
            return ms > 0 ? (int)ms : 0;
        }
    }

    /// <summary>What fired the current lock (skill name), for telemetry.</summary>
    public string LockSource => IsLocked ? _lockSource : "";

    /// <summary>
    /// Record that <paramref name="slot"/> just fired. No-op when the slot has no lock — so
    /// callers can call this unconditionally after any cast. A later, longer lock extends; a
    /// shorter one does not shorten an existing lock.
    /// </summary>
    public void OnFired(SkillSlot slot)
    {
        if (slot.LockMs <= 0) return;
        var until = BotMonotonicClock.Now.Add(TimeSpan.FromMilliseconds(slot.LockMs));
        if (until > _lockedUntil) { _lockedUntil = until; _lockSource = slot.Name; }
    }

    /// <summary>Declare the lock broken (e.g. a dash/blink cancelled the attack animation).</summary>
    public void Clear() { _lockedUntil = TimeSpan.Zero; _lockSource = ""; }
}
