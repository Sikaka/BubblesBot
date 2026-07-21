namespace BubblesBot.Bot.Web;

/// <summary>Outcome of a control action. Reasons block (HTTP 409); warnings inform but don't.</summary>
public sealed record ControlResult(bool Ok, string Status, IReadOnlyList<string> Warnings, IReadOnlyList<string> Reasons)
{
    public static ControlResult Success(string status, params string[] warnings)
        => new(true, status, warnings, []);

    public static ControlResult Blocked(string status, params string[] reasons)
        => new(false, status, [], reasons);
}

/// <summary>
/// Intention-revealing control seam between the web API and the bot, implemented by BotApp.
/// Arming/disarming flips the same persisted <c>BotActive</c> flag as the Insert hotkey via
/// the thread-safe <c>SettingsStore.Mutate</c>; the tick loop's kill-switch (CancelAll when
/// ShouldAct is false) handles input release, so no cross-thread input calls happen here.
/// </summary>
public interface IControlSurface
{
    /// <summary>Preflight (gate available, game attached, in-world) then arm. Foreground loss is a warning, not a blocker.</summary>
    ControlResult Arm(int? mode);

    /// <summary>Always succeeds. Input drops on the next tick via the ShouldAct kill-switch.</summary>
    ControlResult Disarm();

    /// <summary>Switch the active mode. Refuses while armed unless <paramref name="force"/> disarms first (stays disarmed).</summary>
    ControlResult SwitchMode(int mode, bool force);

    /// <summary>Environment/preflight snapshot for the wizard and dashboard.</summary>
    object Meta();
}
