using System.Text.Json;

namespace BubblesBot.Bot.Strategies;

public sealed record GuardianRotaRecoveryState(
    GuardianRotaObjectiveKind Kind,
    string ObjectiveName,
    int PortalEntries,
    int Deaths,
    DateTime UpdatedUtc,
    int? TraversalOriginX = null,
    int? TraversalOriginY = null,
    bool EncounterComplete = false,
    int RotationsCompleted = 0,
    int InvitationsCompleted = 0,
    int GuardianMapsCompleted = 0,
    int ProgressVersion = 0);

/// <summary>Small atomic checkpoint for an encounter that may outlive the bot process.</summary>
public sealed class GuardianRotaRecoveryStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public GuardianRotaRecoveryStore(string? directory = null)
    {
        var root = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BubblesBot", "run-state");
        _path = Path.Combine(root, "guardian-rota-active.json");
    }

    public void Save(GuardianRotaRecoveryState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(
            state with { UpdatedUtc = DateTime.UtcNow }, Options));
        File.Move(temp, _path, overwrite: true);
    }

    public GuardianRotaRecoveryState? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var state = JsonSerializer.Deserialize<GuardianRotaRecoveryState>(
                File.ReadAllText(_path), Options);
            return state is { ObjectiveName.Length: > 0 } ? state : null;
        }
        catch { return null; }
    }

    public void Delete()
    {
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
            if (File.Exists(_path + ".tmp")) File.Delete(_path + ".tmp");
        }
        catch { }
    }
}
