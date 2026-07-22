namespace BubblesBot.Bot.Strategies;

/// <summary>
/// Built-in atlas-node data: map name → AtlasNodes data index, the value the map-device flow
/// needs to click the node on the atlas canvas. These are per-patch game facts validated live
/// by us — a strategy references a node by name only, and names outside this catalog surface
/// as validation warnings and fail closed at the device.
///
/// <para>The UI prefix (data index → canvas child index) stays in MapDeviceSystem because it
/// is a property of the current atlas canvas layout, not of the node.</para>
/// </summary>
public static class AtlasNodeCatalog
{
    // Values are AtlasNodes data indices. The canvas prefix is deliberately owned by
    // MapDeviceSystem because it can drift independently of this file ordering.
    private static readonly Dictionary<string, int> Nodes =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["City Square"] = 36,
            ["Strand"] = 34,        // POEMCP Files.AtlasNodes, Standard, 2026-07-20
            ["Jungle Valley"] = 64,
            ["Pit of the Chimera"] = 100,
            ["Lair of the Hydra"] = 101,
            ["Maze of the Minotaur"] = 102,
            ["Forge of the Phoenix"] = 103,
        };

    public static bool IsSupported(string mapName)
    {
        lock (Nodes) return Nodes.ContainsKey(mapName.Trim());
    }

    public static bool TryGetDataIndex(string mapName, out int dataIndex)
    {
        lock (Nodes) return Nodes.TryGetValue(mapName.Trim(), out dataIndex);
    }

    /// <summary>
    /// Adds an index learned from an exact, ownership-proven Atlas-node tooltip. Runtime
    /// observations are deliberately process-local: every activation still re-verifies the
    /// selected map name before a consumable can be staged or activated.
    /// </summary>
    public static void Observe(string mapName, int dataIndex)
    {
        if (string.IsNullOrWhiteSpace(mapName) || dataIndex < 0) return;
        lock (Nodes) Nodes[mapName.Trim()] = dataIndex;
    }

    public static IReadOnlyCollection<string> SupportedNames
    {
        get { lock (Nodes) return Nodes.Keys.ToArray(); }
    }
}
