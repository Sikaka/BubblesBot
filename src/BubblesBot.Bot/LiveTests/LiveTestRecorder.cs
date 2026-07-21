using System.Text.Json;
using BubblesBot.Bot.Diagnostics;
using BubblesBot.Core;

namespace BubblesBot.Bot.LiveTests;

public sealed class LiveTestRecorder : IDisposable
{
    private static readonly JsonSerializerOptions CompactJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly StreamWriter _timeline;
    private bool _disposed;

    public string EvidenceDirectory { get; }

    public LiveTestRecorder(
        LiveTestOptions options,
        ILiveTestCase test,
        ProcessHandle process,
        nint ingameDataAddress,
        nint ingameStateAddress)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
        var safeId = string.Concat(test.Id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        EvidenceDirectory = Path.Combine(options.ArtifactRoot, safeId, $"{stamp}-{Guid.NewGuid():N}"[..(stamp.Length + 9)]);
        Directory.CreateDirectory(EvidenceDirectory);

        _timeline = new StreamWriter(
            new FileStream(Path.Combine(EvidenceDirectory, "timeline.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };

        WriteIndented("run.json", new
        {
            startedAtUtc = DateTime.UtcNow,
            eventSessionId = EventLog.SessionId,
            test = new
            {
                test.Id,
                test.Name,
                test.Description,
                test.ManualSetup,
                mutation = test.Mutation.ToString(),
                test.DrivesInput,
            },
            options = new
            {
                command = options.Command.ToString(),
                phase = options.Phase?.ToString(),
                options.Armed,
                options.Commit,
                options.SetupConfirmed,
                expectedCharacterConfigured = !string.IsNullOrWhiteSpace(options.ExpectedCharacter),
                expectedAreaHash = options.ExpectedAreaHash is { } h ? $"0x{h:X8}" : null,
                options.TimeoutSeconds,
                options.CountdownSeconds,
                options.Iterations,
            },
            process = new
            {
                process.ProcessId,
                process.ProcessName,
                process.ModulePath,
                mainModuleBase = $"0x{(long)process.MainModuleBase:X16}",
                process.MainModuleSize,
                ingameDataAddress = $"0x{(long)ingameDataAddress:X16}",
                ingameStateAddress = $"0x{(long)ingameStateAddress:X16}",
            },
            environment = new
            {
                os = Environment.OSVersion.ToString(),
                framework = Environment.Version.ToString(),
            },
        });

        EventLog.EntryWritten += OnEvent;
        Record("lifecycle", "run-started", true, $"evidence={EvidenceDirectory}");
    }

    public void Record(
        string kind,
        string label,
        bool? passed,
        string detail,
        IReadOnlyDictionary<string, object?>? data = null)
        => WriteTimeline(new
        {
            atUtc = DateTime.UtcNow,
            monotonic = System.Diagnostics.Stopwatch.GetTimestamp(),
            kind,
            label,
            passed,
            detail,
            data,
        });

    public void Complete(
        LiveTestCaseResult result,
        int passedChecks,
        int failedChecks,
        TimeSpan elapsed,
        string? exception = null)
    {
        Record("lifecycle", "run-completed", result.Outcome == LiveTestOutcome.Passed,
            $"{result.Outcome}: {result.Classification}: {result.Summary}");
        WriteIndented("result.json", new
        {
            completedAtUtc = DateTime.UtcNow,
            outcome = result.Outcome.ToString(),
            result.Classification,
            result.Summary,
            passedChecks,
            failedChecks,
            elapsedMs = elapsed.TotalMilliseconds,
            exception,
        });
    }

    private void OnEvent(EventLog.LogEntry entry)
        => WriteTimeline(new
        {
            atUtc = entry.At,
            monotonic = entry.MonotonicTimestamp,
            kind = "bot-event",
            entry.Seq,
            entry.SessionId,
            entry.RunId,
            entry.TickId,
            areaHash = $"0x{entry.AreaHash:X8}",
            entry.Mode,
            entry.Category,
            entry.EventType,
            severity = entry.Severity.ToString(),
            entry.Message,
            entry.Data,
        });

    private void WriteTimeline(object value)
    {
        lock (_sync)
        {
            if (_disposed) return;
            _timeline.WriteLine(JsonSerializer.Serialize(value, CompactJson));
        }
    }

    private void WriteIndented(string filename, object value)
    {
        var json = JsonSerializer.Serialize(value, IndentedJson);
        File.WriteAllText(Path.Combine(EvidenceDirectory, filename), json);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            EventLog.EntryWritten -= OnEvent;
            _timeline.Dispose();
        }
    }
}
