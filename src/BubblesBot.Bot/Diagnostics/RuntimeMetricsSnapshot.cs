namespace BubblesBot.Bot.Diagnostics;

/// <summary>Metrics for one completed bot tick. Published with the coherent status frame.</summary>
public sealed record RuntimeMetricsSnapshot(
    long TickId,
    long MonotonicTimestamp,
    double TickIntervalMs,
    double TickDurationMs,
    double WorldDurationMs,
    double EntityDurationMs,
    bool WorldRefreshed,
    bool TickOverBudget,
    long Reads,
    long BytesRead,
    long FailedReads,
    long TotalReads,
    long TotalBytesRead,
    long TotalFailedReads,
    long SettingsVersion)
{
    public static RuntimeMetricsSnapshot Empty { get; } = new(
        0, 0, 0, 0, 0, 0, false, false, 0, 0, 0, 0, 0, 0, 0);
}
