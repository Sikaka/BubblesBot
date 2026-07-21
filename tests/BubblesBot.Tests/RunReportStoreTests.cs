using BubblesBot.Bot.Diagnostics;

namespace BubblesBot.Tests;

public sealed class RunReportStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "BubblesBot.Tests", Guid.NewGuid().ToString("N"));

    private static RunReport Report(string runId, DateTime endedUtc, string result = "completed", float chaos = 100f)
        => new(runId, "session1", 4, "Map farming", "sid", "Cloister", "Char", "Mirage", "City Square",
            endedUtc.AddMinutes(-5), endedUtc, 300, result, "", 1, 1, chaos, chaos, chaos * 12, 3, result == "died" ? 1 : 0);

    [Fact]
    public void AppendsAndReloadsAcrossInstances()
    {
        var store = new RunReportStore(_dir);
        store.Report(Report("r1", new DateTime(2026, 7, 15, 10, 0, 0, DateTimeKind.Utc)));
        store.Report(Report("r2", new DateTime(2026, 7, 15, 11, 0, 0, DateTimeKind.Utc)));

        var reloaded = new RunReportStore(_dir);
        var runs = reloaded.Query(null, null, 100);
        Assert.Equal(2, runs.Count);
        Assert.Equal("r2", runs[0].RunId);   // most-recent-first
        Assert.Equal("r1", runs[1].RunId);
    }

    [Fact]
    public void QueryFiltersByTimeWindowAndLimit()
    {
        var store = new RunReportStore(_dir);
        for (var i = 0; i < 5; i++)
            store.Report(Report($"r{i}", new DateTime(2026, 7, 15, 10 + i, 0, 0, DateTimeKind.Utc)));

        Assert.Equal(2, store.Query(null, null, 2).Count);
        var windowed = store.Query(new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc), null, 100);
        Assert.All(windowed, r => Assert.True(r.EndedUtc >= new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(3, windowed.Count);   // r2 @12:00, r3 @13:00, r4 @14:00
    }

    [Fact]
    public void GetByIdReturnsMatch()
    {
        var store = new RunReportStore(_dir);
        store.Report(Report("target", DateTime.UtcNow));
        Assert.NotNull(store.GetById("target"));
        Assert.Null(store.GetById("missing"));
    }

    [Fact]
    public void SummaryAggregatesCompletedRuns()
    {
        var store = new RunReportStore(_dir);
        store.Report(Report("r1", DateTime.UtcNow, "completed", 100f));
        store.Report(Report("r2", DateTime.UtcNow, "completed", 50f));
        store.Report(Report("r3", DateTime.UtcNow, "died", 10f));

        var json = System.Text.Json.JsonSerializer.Serialize(store.Summary());
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(3, doc.RootElement.GetProperty("runs").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("mapsCompleted").GetInt32());
        Assert.Equal(150f, doc.RootElement.GetProperty("totalChaos").GetSingle());
        Assert.Equal(1, doc.RootElement.GetProperty("deaths").GetInt32());
    }

    [Fact]
    public void CorruptTrailingLineIsSkippedOnLoad()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, $"{DateTime.UtcNow:yyyy-MM}.jsonl");
        var good = System.Text.Json.JsonSerializer.Serialize(
            Report("good", DateTime.UtcNow), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        File.WriteAllText(path, good + "\n{ partial broken line");

        var store = new RunReportStore(_dir);
        Assert.Single(store.Query(null, null, 100));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
