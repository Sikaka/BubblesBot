namespace BubblesBot.Bot.Settings;

/// <summary>
/// One global allowance for slow clients. It extends observation windows without stretching
/// retry throttles, and converts the allowance into a bounded number of additional idempotent
/// attempts for callers that explicitly opt in.
/// </summary>
public static class LatencyPolicy
{
    public const int MaximumAllowanceMs = 10_000;
    public const int MaximumPerAttemptAllowanceMs = 1_000;

    public static int AllowanceMs(BotSettings settings)
        => AllowanceMs(settings.ActionLatencyAllowanceMs);

    public static int AllowanceMs(int configuredMs)
        => Math.Clamp(configuredMs, 0, MaximumAllowanceMs);

    public static int TimeoutMs(int baselineMs, BotSettings settings)
        => TimeoutMs(baselineMs, settings.ActionLatencyAllowanceMs);

    public static int TimeoutMs(int baselineMs, int configuredAllowanceMs)
        => checked(Math.Max(1, baselineMs)
            + Math.Min(AllowanceMs(configuredAllowanceMs), MaximumPerAttemptAllowanceMs));

    public static double TimeoutSeconds(double baselineSeconds, BotSettings settings)
        => Math.Max(0.001, baselineSeconds) + AllowanceMs(settings) / 1000.0;

    public static int RetryLimit(int baselineAttempts, BotSettings settings, int maxExtraAttempts = 6)
    {
        var extra = (AllowanceMs(settings) + 999) / 1000;
        return Math.Max(1, baselineAttempts) + Math.Min(Math.Max(0, maxExtraAttempts), extra);
    }
}
