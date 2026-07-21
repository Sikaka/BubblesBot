using System.Diagnostics;

namespace BubblesBot.Bot.Diagnostics;

public enum EventSeverity { Debug, Info, Warning, Error, Critical }

/// <summary>Process-wide structured event ring with correlation context.</summary>
public static class EventLog
{
    public const int Capacity = 1024;
    public static bool ConsoleEnabled { get; set; }

    public sealed record Context(string SessionId, long TickId, uint AreaHash, string Mode, string? RunId);

    public sealed record LogEntry(
        long Seq,
        DateTime At,
        long MonotonicTimestamp,
        string SessionId,
        string? RunId,
        long TickId,
        uint AreaHash,
        string Mode,
        string Category,
        string EventType,
        EventSeverity Severity,
        string Message,
        IReadOnlyDictionary<string, object?>? Data);

    private static readonly object Sync = new();
    private static readonly LogEntry[] Ring = new LogEntry[Capacity];
    private static readonly string Session = Guid.NewGuid().ToString("N");
    private static Context _context = new(Session, 0, 0, "startup", null);
    private static int _head;
    private static int _count;
    private static long _seq;

    public static event Action<LogEntry>? EntryWritten;
    public static string SessionId => Session;

    public static void SetContext(long tickId, uint areaHash, string mode, string? runId = null)
        => Volatile.Write(ref _context, new Context(Session, tickId, areaHash, mode, runId));

    public static void Log(string category, string message)
        => Emit(category, "message", EventSeverity.Info, message);

    public static void Emit(
        string category,
        string eventType,
        EventSeverity severity,
        string message,
        IReadOnlyDictionary<string, object?>? data = null)
    {
        LogEntry entry;
        var context = Volatile.Read(ref _context);
        lock (Sync)
        {
            entry = new LogEntry(
                ++_seq,
                DateTime.UtcNow,
                Stopwatch.GetTimestamp(),
                context.SessionId,
                context.RunId,
                context.TickId,
                context.AreaHash,
                context.Mode,
                category,
                eventType,
                severity,
                message,
                data);
            Ring[_head] = entry;
            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
        }

        if (ConsoleEnabled)
            Console.WriteLine($"[{entry.At:HH:mm:ss.fff}] [{severity}] [{category}] {message}");
        EntryWritten?.Invoke(entry);
    }

    public static IReadOnlyList<LogEntry> Snapshot()
    {
        lock (Sync)
        {
            if (_count == 0) return Array.Empty<LogEntry>();
            var result = new LogEntry[_count];
            var start = (_head - _count + Capacity) % Capacity;
            for (var i = 0; i < _count; i++) result[i] = Ring[(start + i) % Capacity];
            return result;
        }
    }

    public static IReadOnlyList<LogEntry> Recent(int n)
    {
        var snap = Snapshot();
        if (n >= snap.Count) return snap.Reverse().ToArray();
        var result = new LogEntry[n];
        var start = snap.Count - n;
        for (var i = 0; i < n; i++) result[n - 1 - i] = snap[start + i];
        return result;
    }
}
