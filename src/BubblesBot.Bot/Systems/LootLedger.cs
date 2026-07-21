using BubblesBot.Bot.Behaviors.Loot;

namespace BubblesBot.Bot.Systems;

/// <summary>
/// Confirmed-pickup ledger for run reporting. Values are conservative; the plausible maximum
/// is retained separately for unidentified/shared-art diagnostics and never silently counted
/// as realized profit.
/// </summary>
public sealed class LootLedger
{
    public sealed record Entry(
        DateTime At,
        string Name,
        string Category,
        int StackCount,
        float ChaosValue,
        float MaxChaosValue,
        string Reason);

    public sealed record SnapshotData(
        int Pickups,
        float TotalChaos,
        float MaxPlausibleChaos,
        double ChaosPerHour,
        IReadOnlyDictionary<string, float> ByCategory,
        IReadOnlyList<Entry> Recent);

    private readonly TimeSpan _startedAt = BotMonotonicClock.Now;
    private readonly List<Entry> _recent = new();
    private readonly Dictionary<string, float> _byCategory = new(StringComparer.OrdinalIgnoreCase);
    private float _totalChaos;
    private float _maxPlausibleChaos;
    private int _pickups;

    public void Record(string name, int stackCount, LootEvaluation evaluation)
    {
        stackCount = Math.Max(1, stackCount);
        var conservative = Math.Max(0, evaluation.ChaosValue) * stackCount;
        var plausible = Math.Max(evaluation.ChaosValue, evaluation.MaxChaosValue) * stackCount;
        _totalChaos += conservative;
        _maxPlausibleChaos += plausible;
        _pickups++;
        _byCategory[evaluation.Category] = _byCategory.GetValueOrDefault(evaluation.Category) + conservative;
        var entry = new Entry(DateTime.UtcNow, name, evaluation.Category, stackCount,
            conservative, plausible, evaluation.Reason);
        _recent.Add(entry);
        if (_recent.Count > 200) _recent.RemoveAt(0);

        Diagnostics.EventLog.Emit("loot", "loot.pickup-confirmed", Diagnostics.EventSeverity.Info,
            $"picked up {name}: {conservative:F1}c ({evaluation.Reason})",
            new Dictionary<string, object?>
            {
                ["name"] = name,
                ["category"] = evaluation.Category,
                ["stackCount"] = stackCount,
                ["chaosValue"] = conservative,
                ["maxPlausibleChaos"] = plausible,
                ["reason"] = evaluation.Reason,
            });
    }

    public SnapshotData Snapshot()
    {
        var hours = Math.Max(1.0 / 3600.0, (BotMonotonicClock.Now - _startedAt).TotalHours);
        return new SnapshotData(
            _pickups,
            _totalChaos,
            _maxPlausibleChaos,
            _totalChaos / hours,
            _byCategory.ToDictionary(x => x.Key, x => x.Value),
            _recent.TakeLast(40).Reverse().ToArray());
    }

    /// <summary>Allocation-free subset for the high-frequency in-game overlay.</summary>
    public (int Pickups, float TotalChaos, double ChaosPerHour) OverlaySummary()
    {
        var hours = Math.Max(1.0 / 3600.0, (BotMonotonicClock.Now - _startedAt).TotalHours);
        return (_pickups, _totalChaos, _totalChaos / hours);
    }
}
