using System.Text.Json;
using System.Threading.Channels;
using BubblesBot.Core.Game;
using BubblesBot.Core.Knowledge;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Diagnostics;

public sealed record FlightPlayer(int X, int Y, int Hp, int HpMax);

public sealed record FlightEntity(
    uint Id,
    string Path,
    int X,
    int Y,
    int Hp,
    int HpMax,
    ObservationTruth Targetable,
    ObservationTruth Allied,
    ObservationTruth Dormant,
    ObservationTruth LifeReadable,
    bool Moving,
    bool Stale,
    EntityListReader.EntityKind Kind,
    EntityDisposition Disposition,
    EntityCache.Tier Tier,
    ObservationTruth ShrineAvailable = ObservationTruth.Unknown,
    bool RitualCurrentStateKnown = false,
    long RitualCurrentState = 0,
    bool RitualInteractionEnabledKnown = false,
    long RitualInteractionEnabled = 0,
    IReadOnlyList<long>? SimulacrumRawStates = null,
    bool SimulacrumActiveKnown = false,
    long SimulacrumActive = 0,
    bool SimulacrumGoodbyeKnown = false,
    long SimulacrumGoodbye = 0,
    bool SimulacrumWaveKnown = false,
    long SimulacrumWave = 0,
    bool AreaTransitionIdentityReadable = false,
    AreaTransitionType AreaTransitionType = AreaTransitionType.Normal,
    ushort AreaTransitionAreaId = 0,
    string DestinationAreaId = "",
    string DestinationAreaName = "");

public sealed record WorldRecordFrame(
    long TickId,
    long MonotonicTimestamp,
    string? RunId,
    uint AreaHash,
    string GameState,
    int ModeId,
    string Mode,
    string Decision,
    FlightPlayer? Player,
    EntityCache.ScanHealth EntityScan,
    RuntimeMetricsSnapshot Runtime,
    IReadOnlyList<FlightEntity> Entities);

/// <summary>
/// Non-blocking rolling JSONL recorder. Events are written immediately; world frames are
/// reduced to changed entities with a periodic full keyframe for deterministic reconstruction.
/// </summary>
public sealed class FlightRecorder : IDisposable
{
    private const long MaxSegmentBytes = 32L * 1024 * 1024;
    private const int MaxSegments = 8;
    private const int KeyframeEvery = 30;
    private readonly Channel<object> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writer;
    private readonly string _directory;
    private readonly Dictionary<uint, FlightEntity> _lastEntities = new();
    private uint _lastAreaHash;
    private int _framesSinceKeyframe = KeyframeEvery;
    private long _dropped;

    public FlightRecorder()
    {
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BubblesBot", "recordings");
        Directory.CreateDirectory(_directory);
        _channel = Channel.CreateBounded<object>(new BoundedChannelOptions(8192)
        {
            // Wait mode plus TryWrite gives us an explicit false when full without ever
            // blocking the producer; drop modes can report success after discarding data.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        EventLog.EntryWritten += OnEvent;
        _writer = Task.Run(WriterLoopAsync);
    }

    public long DroppedRecords => Interlocked.Read(ref _dropped);
    public string DirectoryPath => _directory;

    private void OnEvent(EventLog.LogEntry entry) => Enqueue(new { kind = "event", entry });

    public void RecordWorldFrame(WorldRecordFrame frame)
    {
        var areaChanged = frame.AreaHash != _lastAreaHash;
        var keyframe = areaChanged || _framesSinceKeyframe >= KeyframeEvery;
        if (areaChanged)
        {
            _lastEntities.Clear();
            _lastAreaHash = frame.AreaHash;
        }

        var changed = new List<FlightEntity>();
        var seen = new HashSet<uint>();
        foreach (var entity in frame.Entities)
        {
            seen.Add(entity.Id);
            if (keyframe || !_lastEntities.TryGetValue(entity.Id, out var prior) || prior != entity)
                changed.Add(entity);
            _lastEntities[entity.Id] = entity;
        }

        var removed = keyframe
            ? Array.Empty<uint>()
            : _lastEntities.Keys.Where(id => !seen.Contains(id)).ToArray();
        foreach (var id in removed) _lastEntities.Remove(id);

        Enqueue(new
        {
            kind = keyframe ? "world.keyframe" : "world.delta",
            sessionId = EventLog.SessionId,
            frame.TickId,
            frame.MonotonicTimestamp,
            frame.RunId,
            frame.AreaHash,
            frame.GameState,
            frame.ModeId,
            frame.Mode,
            frame.Decision,
            frame.Player,
            frame.EntityScan,
            frame.Runtime,
            changedEntities = changed,
            removedEntityIds = removed,
        });
        _framesSinceKeyframe = keyframe ? 0 : _framesSinceKeyframe + 1;
    }

    private void Enqueue(object item)
    {
        if (!_channel.Writer.TryWrite(item)) Interlocked.Increment(ref _dropped);
    }

    private async Task WriterLoopAsync()
    {
        var segment = 0;
        StreamWriter? writer = null;
        try
        {
            while (await _channel.Reader.WaitToReadAsync(_cts.Token))
            {
                while (_channel.Reader.TryRead(out var item))
                {
                    writer ??= OpenWriter(segment);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(item));
                    if (writer.BaseStream.Position >= MaxSegmentBytes)
                    {
                        await writer.FlushAsync();
                        writer.Dispose();
                        writer = OpenWriter(++segment);
                    }
                }
                if (writer is not null) await writer.FlushAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Flight recorder stopped: {ex.Message}");
        }
        finally
        {
            if (writer is not null)
            {
                try { await writer.FlushAsync(); } catch { }
                writer.Dispose();
            }
        }
    }

    private StreamWriter OpenWriter(int segment)
    {
        // True rolling retention: once a ninth segment is opened, delete the oldest segment
        // from this session. At 32 MiB each this caps one process session near 256 MiB while
        // retaining the most recent incident context.
        var expired = segment - MaxSegments;
        if (expired >= 0)
        {
            var expiredPath = Path.Combine(_directory,
                $"session-{EventLog.SessionId}-{expired:D3}.jsonl");
            try { File.Delete(expiredPath); } catch { }
        }
        var name = $"session-{EventLog.SessionId}-{segment:D3}.jsonl";
        return new StreamWriter(new FileStream(
            Path.Combine(_directory, name), FileMode.Append, FileAccess.Write, FileShare.Read,
            bufferSize: 64 * 1024, useAsync: true));
    }

    public void Dispose()
    {
        EventLog.EntryWritten -= OnEvent;
        _channel.Writer.TryComplete();
        try { _writer.Wait(2000); } catch { }
        _cts.Cancel();
        _cts.Dispose();
    }
}
