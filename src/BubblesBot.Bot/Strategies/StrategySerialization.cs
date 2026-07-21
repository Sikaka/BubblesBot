using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BubblesBot.Bot.Strategies;

/// <summary>
/// A strategy file could not be read: invalid JSON, an unknown mechanic type or field, or a
/// schema version this build cannot handle. Import/load paths catch this and surface the
/// message; nothing partial is ever produced.
/// </summary>
public sealed class StrategyFormatException : Exception
{
    public StrategyFormatException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// The single serialization boundary for strategy documents. Parsing is fail-closed by
/// construction: unknown mechanic discriminators and unmapped members throw (every DTO carries
/// <c>JsonUnmappedMemberHandling.Disallow</c>), unknown enum strings throw, and the version
/// gate runs before deserialization so newer files are rejected rather than half-read.
/// </summary>
public static class StrategySerialization
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        // Shared/hand-edited files may not put the "type" discriminator first.
        AllowOutOfOrderMetadataProperties = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Version-gate, migrate, then strictly deserialize. Throws <see cref="StrategyFormatException"/> on any defect.</summary>
    public static FarmingStrategy Parse(string json)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject
                ?? throw new StrategyFormatException("strategy root must be a JSON object");
        }
        catch (JsonException ex)
        {
            throw new StrategyFormatException($"invalid JSON: {ex.Message}", ex);
        }

        root = StrategyMigrations.Apply(root);

        try
        {
            return root.Deserialize<FarmingStrategy>(JsonOptions)
                ?? throw new StrategyFormatException("strategy document is empty");
        }
        catch (JsonException ex)
        {
            throw new StrategyFormatException($"unrecognized or malformed field: {ex.Message}", ex);
        }
        catch (NotSupportedException ex)
        {
            throw new StrategyFormatException($"unsupported mechanic type: {ex.Message}", ex);
        }
    }

    public static string Serialize(FarmingStrategy strategy)
        => JsonSerializer.Serialize(strategy, JsonOptions);

    /// <summary>Deep clone so store snapshots and caller edits never alias.</summary>
    public static FarmingStrategy Clone(FarmingStrategy strategy)
        => JsonSerializer.Deserialize<FarmingStrategy>(Serialize(strategy), JsonOptions)!;
}
