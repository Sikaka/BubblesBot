using System.Reflection;
using System.Text.Json;

namespace BubblesBot.Core.Knowledge;

/// <summary>
/// Maps unidentified-unique resource paths (the <c>Art/2DItems/...</c> texture path on the
/// item's RenderItem component) to the candidate list of unique names that share that art.
/// <see cref="PriceCatalog"/> looks up each candidate and the loot filter takes the
/// <em>highest</em> price across them — uniques sharing an art slot are often the cheap +
/// expensive pair (e.g. Kaom's Heart / Replica Kaom's Heart), so optimistic pricing is the
/// safe call.
///
/// <para>Source: AutoExile's <c>uniqueArtMapping.json</c>, embedded into the Core assembly so
/// the bot never needs filesystem access at startup. Update the resource file when GGG ships
/// new uniques; the bot keeps working on stale data (unknown art → empty candidate list →
/// unidentified unique falls through to "always take" in the value filter).</para>
/// </summary>
public sealed class UniqueArtMapping
{
    private static readonly Lazy<UniqueArtMapping> _shared = new(() => Load(), isThreadSafe: true);

    /// <summary>Process-wide singleton. Lazy-loaded on first access; ~100 KB embedded JSON.</summary>
    public static UniqueArtMapping Shared => _shared.Value;

    private readonly Dictionary<string, IReadOnlyList<string>> _byPath;

    private UniqueArtMapping(Dictionary<string, IReadOnlyList<string>> byPath)
    {
        _byPath = byPath;
    }

    /// <summary>Number of art paths we know about. Useful for diagnostics / dashboard.</summary>
    public int EntryCount => _byPath.Count;

    /// <summary>
    /// Look up the candidate unique names for a given <c>RenderItem.ResourcePath</c>. Returns
    /// an empty list when the path isn't in the table — caller should treat that as "we don't
    /// know what this is, assume it's potentially valuable."
    /// </summary>
    public IReadOnlyList<string> Resolve(string resourcePath)
    {
        if (string.IsNullOrEmpty(resourcePath)) return Array.Empty<string>();
        // The texture path from PoE memory may include leading slashes or case differences;
        // AutoExile stores them with the canonical "Art/2DItems/..." prefix. Try exact match
        // first (fast path) then a normalized retry.
        if (_byPath.TryGetValue(resourcePath, out var direct)) return direct;
        var trimmed = resourcePath.TrimStart('/');
        if (_byPath.TryGetValue(trimmed, out var trimmedHit)) return trimmedHit;
        return Array.Empty<string>();
    }

    private static UniqueArtMapping Load()
    {
        var asm = typeof(UniqueArtMapping).Assembly;
        var resName = $"{asm.GetName().Name}.Resources.uniqueArtMapping.json";
        using var stream = asm.GetManifestResourceStream(resName);
        if (stream is null)
            return new UniqueArtMapping(new Dictionary<string, IReadOnlyList<string>>(0));

        try
        {
            // Each key maps to either a JSON array of names or (defensively) a single string.
            using var doc = JsonDocument.Parse(stream);
            var map = new Dictionary<string, IReadOnlyList<string>>(doc.RootElement.GetPropertyCount(), StringComparer.OrdinalIgnoreCase);
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind == JsonValueKind.Array)
                {
                    var names = new List<string>(entry.Value.GetArrayLength());
                    foreach (var n in entry.Value.EnumerateArray())
                        if (n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } s) names.Add(s);
                    if (names.Count > 0) map[entry.Name] = names;
                }
                else if (entry.Value.ValueKind == JsonValueKind.String && entry.Value.GetString() is { Length: > 0 } single)
                {
                    map[entry.Name] = new[] { single };
                }
            }
            return new UniqueArtMapping(map);
        }
        catch
        {
            return new UniqueArtMapping(new Dictionary<string, IReadOnlyList<string>>(0));
        }
    }
}
