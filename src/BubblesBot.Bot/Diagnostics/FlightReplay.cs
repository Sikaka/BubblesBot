using System.Text.Json;
using BubblesBot.Bot.Systems;
using BubblesBot.Core.Knowledge;

namespace BubblesBot.Bot.Diagnostics;

public sealed record ReplayWorldFrame(
    long TickId,
    long MonotonicTimestamp,
    uint AreaHash,
    int ModeId,
    string Mode,
    FlightPlayer? Player,
    IReadOnlyList<FlightEntity> Entities);

public sealed record ReplayIntent(long TickId, string Kind, uint? TargetId, string Evidence);

public sealed class ReplayClock
{
    public long Timestamp { get; private set; }
    public void AdvanceTo(long timestamp)
    {
        if (timestamp < Timestamp) throw new InvalidDataException("recording timestamps moved backwards");
        Timestamp = timestamp;
    }
}

public sealed class ReplayInputSink
{
    private readonly List<ReplayIntent> _intents = new();
    public IReadOnlyList<ReplayIntent> Intents => _intents;
    public void Emit(ReplayIntent intent) => _intents.Add(intent);
}

/// <summary>
/// Deterministic offline version of the map-clear target/explore decision boundary. It consumes
/// reconstructed flight frames, shares production target semantics, and emits intents into a fake
/// sink without touching Win32 input.
/// </summary>
public sealed class MapClearReplayMode
{
    public ReplayIntent Tick(ReplayWorldFrame frame)
    {
        FlightEntity? best = null;
        long bestDistance2 = long.MaxValue;
        var rejected = new Dictionary<TargetRejectionReason, int>();
        foreach (var entity in frame.Entities)
        {
            var result = TargetEligibility.Evaluate(ToFacts(entity));
            if (!result.Accepted)
            {
                rejected[result.Reason] = rejected.GetValueOrDefault(result.Reason) + 1;
                continue;
            }
            if (frame.Player is null) continue;
            long dx = entity.X - frame.Player.X, dy = entity.Y - frame.Player.Y;
            var distance2 = dx * dx + dy * dy;
            if (distance2 < bestDistance2) { bestDistance2 = distance2; best = entity; }
        }

        return best is { } target
            ? new ReplayIntent(frame.TickId, "attack", target.Id,
                $"nearest eligible target distance={Math.Sqrt(bestDistance2):F1}")
            : new ReplayIntent(frame.TickId, "explore", null,
                rejected.Count == 0
                    ? "no combat entities"
                    : string.Join(",", rejected.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value}")));
    }

    private static TargetFacts ToFacts(FlightEntity entity) => new(
        entity.Kind,
        entity.Disposition,
        entity.Stale,
        entity.Allied,
        entity.LifeReadable,
        entity.Hp,
        entity.HpMax,
        entity.Targetable,
        entity.Dormant,
        Path.GetFileName(entity.Path));
}

public static class FlightReplay
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<ReplayIntent> Run(string path, long? stopAtTick = null)
    {
        var clock = new ReplayClock();
        var sink = new ReplayInputSink();
        var mode = new MapClearReplayMode();
        foreach (var frame in ReadFrames(path))
        {
            if (stopAtTick is { } stop && frame.TickId > stop) break;
            clock.AdvanceTo(frame.MonotonicTimestamp);
            sink.Emit(mode.Tick(frame));
        }
        return sink.Intents;
    }

    public static IEnumerable<ReplayWorldFrame> ReadFrames(string path)
    {
        var files = Directory.Exists(path)
            ? Directory.GetFiles(path, "*.jsonl").OrderBy(x => x, StringComparer.Ordinal).ToArray()
            : [path];
        var entities = new Dictionary<uint, FlightEntity>();
        foreach (var file in files)
        {
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var kind = Get(root, "kind")?.GetString();
                if (kind is not ("world.keyframe" or "world.delta")) continue;
                if (kind == "world.keyframe") entities.Clear();

                if (Get(root, "changedEntities") is { ValueKind: JsonValueKind.Array } changed)
                {
                    foreach (var item in changed.EnumerateArray())
                    {
                        var entity = item.Deserialize<FlightEntity>(JsonOptions);
                        if (entity is not null) entities[entity.Id] = entity;
                    }
                }
                if (Get(root, "removedEntityIds") is { ValueKind: JsonValueKind.Array } removed)
                    foreach (var id in removed.EnumerateArray()) entities.Remove(id.GetUInt32());

                yield return new ReplayWorldFrame(
                    Get(root, "TickId")?.GetInt64() ?? 0,
                    Get(root, "MonotonicTimestamp")?.GetInt64() ?? 0,
                    Get(root, "AreaHash")?.GetUInt32() ?? 0,
                    Get(root, "ModeId")?.GetInt32() ?? 0,
                    Get(root, "Mode")?.GetString() ?? "",
                    Get(root, "Player") is { ValueKind: JsonValueKind.Object } player
                        ? player.Deserialize<FlightPlayer>(JsonOptions) : null,
                    entities.Values.OrderBy(x => x.Id).ToArray());
            }
        }
    }

    private static JsonElement? Get(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var exact)) return exact;
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return null;
    }
}
