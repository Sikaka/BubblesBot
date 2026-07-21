namespace BubblesBot.Bot.Modes;

/// <summary>Pure timing contract for deciding that a map did not spawn a Delirium mirror.</summary>
public static class DeliriumPresencePolicy
{
    public const double MirrorDiscoverySeconds = 5.0;

    public static bool IsNotPresent(bool entityScanHealthy, double observedSeconds)
        => entityScanHealthy && observedSeconds >= MirrorDiscoverySeconds;
}
