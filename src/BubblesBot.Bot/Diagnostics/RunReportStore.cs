using System.Text.Json;
using System.Text.Json.Serialization;

namespace BubblesBot.Bot.Diagnostics;

/// <summary>
/// One completed map/run summary. Persisted as an append-only JSONL line so run history survives
/// restarts (unlike the in-memory LootLedger / mode telemetry). One record per completed map plus
/// a terminal record when a run stops.
/// </summary>
public sealed record RunReport(
    string RunId,
    string SessionId,
    int Mode,
    string ModeName,
    string StrategyId,
    string StrategyName,
    string Profile,
    string League,
    string MapName,
    DateTime StartedUtc,
    DateTime EndedUtc,
    double DurationSec,
    string Result,           // completed | abandoned | stopped | died | disarmed
    string StopReason,
    int MapIndex,            // 1-based position of this map within the session run
    int MapsCompleted,       // session cumulative at emission time
    float LootChaos,         // this map's conservative chaos
    float LootChaosCumulative,
    double ChaosPerHour,
    int ItemsPicked,         // this map's pickups
    int Deaths);

/// <summary>Invoked at map/run boundaries by the run modes. Kept tiny so modes stay decoupled from storage.</summary>
public interface IRunReporter
{
    void Report(RunReport report);
}

/// <summary>
/// Durable run-history store: append-only monthly JSONL under
/// <c>%LOCALAPPDATA%/BubblesBot/runs/YYYY-MM.jsonl</c>, plus an in-memory ring of the most recent
/// records for cheap querying. Writes never throw into the tick loop (a failed append is dropped
/// with an event, not propagated).
/// </summary>
public sealed class RunReportStore : IRunReporter
{
    private const int RecentCapacity = 500;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string _directory;
    private readonly object _lock = new();
    private readonly LinkedList<RunReport> _recent = new();

    public RunReportStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BubblesBot", "runs");
        Directory.CreateDirectory(_directory);
        LoadRecent();
    }

    public void Report(RunReport report)
    {
        lock (_lock)
        {
            _recent.AddFirst(report);
            while (_recent.Count > RecentCapacity) _recent.RemoveLast();
            try
            {
                var path = Path.Combine(_directory, $"{report.EndedUtc:yyyy-MM}.jsonl");
                File.AppendAllText(path, JsonSerializer.Serialize(report, JsonOptions) + "\n");
            }
            catch (IOException ex)
            {
                EventLog.Emit("incident", "run-report.write-failed", EventSeverity.Warning,
                    $"failed to persist run report {report.RunId}: {ex.Message}");
            }
        }
    }

    /// <summary>Most-recent-first history within an optional time window, capped at <paramref name="limit"/>.</summary>
    public IReadOnlyList<RunReport> Query(DateTime? since, DateTime? before, int limit)
    {
        lock (_lock)
        {
            IEnumerable<RunReport> q = _recent;
            if (since is { } s) q = q.Where(r => r.EndedUtc >= s);
            if (before is { } b) q = q.Where(r => r.EndedUtc < b);
            return q.Take(Math.Clamp(limit, 1, RecentCapacity)).ToArray();
        }
    }

    public RunReport? GetById(string runId)
    {
        lock (_lock) return _recent.FirstOrDefault(r => r.RunId == runId);
    }

    /// <summary>Aggregate KPIs over the current recent window (for the dashboard header).</summary>
    public object Summary()
    {
        lock (_lock)
        {
            var completed = _recent.Where(r => r.Result == "completed").ToArray();
            var totalChaos = completed.Sum(r => r.LootChaos);
            var totalHours = completed.Sum(r => r.DurationSec) / 3600.0;
            return new
            {
                runs = _recent.Count,
                mapsCompleted = completed.Length,
                totalChaos,
                chaosPerHour = totalHours > 0 ? totalChaos / totalHours : 0.0,
                deaths = _recent.Sum(r => r.Deaths),
            };
        }
    }

    private void LoadRecent()
    {
        // Load the two newest monthly files (enough to fill the recent ring for the UI).
        var files = Directory.EnumerateFiles(_directory, "*.jsonl")
            .OrderByDescending(f => f)
            .Take(2)
            .Reverse()
            .ToArray();
        var all = new List<RunReport>();
        foreach (var file in files)
        {
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    if (JsonSerializer.Deserialize<RunReport>(line, JsonOptions) is { } report) all.Add(report);
                }
                catch (JsonException)
                {
                    // Skip a corrupt/partial trailing line; never fail startup on history.
                }
            }
        }
        foreach (var report in all.OrderBy(r => r.EndedUtc).TakeLast(RecentCapacity))
            _recent.AddFirst(report);
    }
}
