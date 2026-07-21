using System.Text.Json.Nodes;

namespace BubblesBot.Bot.Strategies;

/// <summary>
/// Schema-version gate and migration pipeline for strategy documents.
///
/// <para>Rules: a file from a newer BubblesBot is rejected outright (never partially read); an
/// older file is upgraded through ordered, pure <c>JsonObject → JsonObject</c> steps before
/// deserialization; a same-version file with unknown members is rejected by the serializer.
/// Each step migrates exactly one version and is unit-testable in isolation.</para>
/// </summary>
public static class StrategyMigrations
{
    public const int CurrentSchemaVersion = 1;
    public const int FirstSchemaVersion = 1;

    /// <summary>Step keyed N migrates a version-N document to version N+1. Empty until v2 exists.</summary>
    private static readonly IReadOnlyDictionary<int, Func<JsonObject, JsonObject>> Steps =
        new Dictionary<int, Func<JsonObject, JsonObject>>();

    /// <summary>
    /// Validate the version stamp and upgrade the node to <see cref="CurrentSchemaVersion"/>.
    /// Throws <see cref="StrategyFormatException"/> for missing stamps, pre-history versions,
    /// and versions newer than this build understands.
    /// </summary>
    public static JsonObject Apply(JsonObject root)
    {
        var versionNode = GetCaseInsensitive(root, "schemaVersion");
        if (versionNode is not JsonValue value || !value.TryGetValue<int>(out var version))
            throw new StrategyFormatException("missing or non-integer schemaVersion");

        if (version > CurrentSchemaVersion)
            throw new StrategyFormatException(
                $"strategy was created by a newer BubblesBot (schemaVersion {version}; this build reads up to {CurrentSchemaVersion})");
        if (version < FirstSchemaVersion)
            throw new StrategyFormatException($"schemaVersion {version} predates the first supported version ({FirstSchemaVersion})");

        while (version < CurrentSchemaVersion)
        {
            if (!Steps.TryGetValue(version, out var step))
                throw new StrategyFormatException($"no migration step registered for schemaVersion {version}");
            root = step(root);
            version++;
            root["schemaVersion"] = version;
        }
        return root;
    }

    private static JsonNode? GetCaseInsensitive(JsonObject obj, string name)
    {
        foreach (var (key, node) in obj)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                return node;
        return null;
    }
}
