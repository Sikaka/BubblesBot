namespace BubblesBot.Bot.Modes;

/// <summary>
/// Pure Delirium-fog pacing policy. The mirror emits an expanding annulus: falling behind its
/// rear edge and outrunning its front edge can both end the encounter. Runtime movement remains
/// exploration-driven; this policy supplies the forward-distance ceiling that periodically holds
/// a very fast character while the front expands.
/// </summary>
public static class DeliriumPacingPolicy
{
    public static float ForwardAllowance(
        double elapsedSeconds, float initialLeadGrid, float maxForwardGridPerSecond)
        => Math.Max(0, initialLeadGrid)
         + (float)Math.Max(0, elapsedSeconds) * Math.Max(0, maxForwardGridPerSecond);

    public static bool ShouldThrottle(
        float distanceFromMirror,
        double elapsedSeconds,
        float initialLeadGrid,
        float maxForwardGridPerSecond)
        => distanceFromMirror > ForwardAllowance(
            elapsedSeconds, initialLeadGrid, maxForwardGridPerSecond);
}
