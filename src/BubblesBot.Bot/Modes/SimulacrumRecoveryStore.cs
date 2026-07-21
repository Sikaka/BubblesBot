using System.Text.Json;

namespace BubblesBot.Bot.Modes;

public readonly record struct SimulacrumRecoveryPoint(int X, int Y);
public readonly record struct SimulacrumRecoveryItem(uint Id, string Name, int X, int Y);

public sealed record SimulacrumRecoveryState(
    uint AreaHash,
    int Wave,
    SimulacrumRecoveryPoint? Monolith,
    SimulacrumRecoveryPoint? Stash,
    SimulacrumRecoveryPoint? Portal,
    SimulacrumRecoveryPoint? RewardAnchor,
    SimulacrumRecoveryItem[] PendingItems);

/// <summary>
/// Crash/restart checkpoint for destructive between-wave state. Starting the next wave
/// deletes every remaining ground item, so an off-screen accepted reward cannot exist only
/// in process memory.
/// </summary>
public sealed class SimulacrumRecoveryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _directory;

    public SimulacrumRecoveryStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BubblesBot", "run-state");
    }

    public void Save(SimulacrumRecoveryState state)
    {
        Directory.CreateDirectory(_directory);
        var path = PathFor(state.AreaHash);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    public SimulacrumRecoveryState? Load(uint areaHash)
    {
        try
        {
            var path = PathFor(areaHash);
            if (!File.Exists(path)) return null;
            var state = JsonSerializer.Deserialize<SimulacrumRecoveryState>(
                File.ReadAllText(path), JsonOptions);
            return state?.AreaHash == areaHash ? state : null;
        }
        catch
        {
            // Corrupt or interrupted recovery data fails closed in the adapter via its
            // mandatory reattach sweep; it must never crash the world tick.
            return null;
        }
    }

    public void Delete(uint areaHash)
    {
        try
        {
            var path = PathFor(areaHash);
            if (File.Exists(path)) File.Delete(path);
            var temp = path + ".tmp";
            if (File.Exists(temp)) File.Delete(temp);
        }
        catch
        {
            // A later Save/Delete attempt will retry; destructive start remains guarded by
            // the in-memory pending set and reattach sweep.
        }
    }

    private string PathFor(uint areaHash)
        => Path.Combine(_directory, $"simulacrum-{areaHash:X8}.json");
}
