using BubblesBot.Bot.Input;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Systems;

/// <summary>
/// Death detection + resurrect. When the resurrect panel is up (the death signal), clicks
/// "Resurrect at Checkpoint" (→ hideout on an atlas map), verified by the panel closing. Runs
/// ABOVE the active mode: while dead, mode dispatch is skipped so the bot doesn't fire skills /
/// walk into a corpse.
///
/// <para><b>Post-revive policy:</b> the active mode owns the confirmed
/// <see cref="Result.JustRevived"/> edge. Simulacrum has a bounded existing-portal recovery;
/// modes without a validated recovery contract disarm in the caller.</para>
/// </summary>
public sealed class ReviveSystem
{
    public enum Result { NotDead, Reviving, JustRevived }

    private const double ClickCooldownMs = 900; // let the click register / panel animate before re-click

    private bool _wasDead;
    private TimeSpan _lastClickAt;

    /// <summary>Total deaths observed this session.</summary>
    public int Deaths { get; private set; }

    public void Reset()
    {
        _wasDead = false;
        _lastClickAt = TimeSpan.Zero;
    }

    public Result Tick(GameSnapshot snapshot, IInputRouter input)
    {
        var rp = snapshot.ResurrectPanel;
        if (!rp.IsVisible)
        {
            if (_wasDead) { _wasDead = false; return Result.JustRevived; }
            return Result.NotDead;
        }

        if (!_wasDead)
        {
            _wasDead = true;
            Deaths++;
            Diagnostics.EventLog.Log("revive", $"death #{Deaths} — resurrecting at checkpoint");
        }

        // Click the checkpoint button, throttled. Rect is window-relative → add the window origin.
        var now = BotMonotonicClock.Now;
        if ((now - _lastClickAt).TotalMilliseconds >= ClickCooldownMs)
        {
            var rect = rp.CheckpointButtonRect();
            if (rect is { } r)
            {
                var (ax, ay) = snapshot.Window.ToScreen(r.CenterX, r.CenterY);
                var ticket = input.Click(ax, ay, ClickIntent.InteractUi, "resurrect at checkpoint",
                    expectResolved: () => !snapshot.ResurrectPanel.IsVisible);
                if (ticket.Accepted) _lastClickAt = now;
            }
        }
        return Result.Reviving;
    }
}
