using System.Net.Http;
using System.Text.Json;

namespace BubblesBot.Core.Knowledge;

/// <summary>One row from poe.ninja's item overview. Variant fields are intentionally retained.</summary>
public sealed record ItemPriceVariant(
    string Name,
    string Category,
    float ChaosValue,
    string? Variant = null,
    int? GemLevel = null,
    int? GemQuality = null,
    int? LevelRequired = null,
    bool? Corrupted = null,
    string? DetailsId = null,
    int ListingCount = 0);

/// <summary>
/// A price with its matching evidence. <see cref="ChaosValue"/> is conservative when more
/// than one row matches so run-profit telemetry does not report the most expensive possible
/// roll as though it were known.
/// </summary>
public readonly record struct PriceQuote(
    float ChaosValue,
    float MinChaosValue,
    float MaxChaosValue,
    int MatchingVariants,
    string Match)
{
    public bool IsKnown => MatchingVariants > 0 && MaxChaosValue > 0;
    public static PriceQuote Unknown(string match) => new(0, 0, 0, 0, match);
}

/// <summary>
/// Thread-safe item value catalog backed by poe.ninja's current PoE 1 economy endpoints.
/// The flat name lookup is retained for generic loot, while variant rows support gems,
/// cluster jewels, and roll-sensitive uniques such as Voices.
/// </summary>
public sealed class PriceCatalog
{
    // Bump whenever the fetched category set or serialized meaning changes. A fresh cache
    // built before a new economy category was added must not suppress the next refresh.
    private const int CacheSchemaVersion = 2;
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders = { { "User-Agent", "BubblesBot/0.1 (poe1 research)" } },
    };

    private static readonly string[] ExchangeEndpoints =
    {
        "Currency", "Fragment", "DivinationCard", "Scarab", "Fossil", "Resonator",
        "Essence", "Oil", "DeliriumOrb", "Omen",
    };

    private static readonly string[] ItemEndpoints =
    {
        "UniqueWeapon", "UniqueArmour", "UniqueAccessory", "UniqueFlask", "UniqueJewel",
        "Map", "BlightedMap", "BlightRavagedMap",
        "SkillGem", "ClusterJewel",
    };

    private readonly string _league;
    private readonly TimeSpan _refreshEvery;
    private readonly string _cachePath;
    private Dictionary<string, float> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, ItemPriceVariant[]> _variantsByName = new(StringComparer.OrdinalIgnoreCase);

    public DateTime LastRefreshedAt { get; private set; }
    public string? LastError { get; private set; }
    public int EntryCount => Volatile.Read(ref _byKey).Count;
    public int VariantCount => Volatile.Read(ref _variantsByName).Values.Sum(x => x.Length);

    public PriceCatalog(string league, TimeSpan? refreshEvery = null, string? cachePath = null)
    {
        _league = league;
        _refreshEvery = refreshEvery ?? TimeSpan.FromHours(6);
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BubblesBot");
        Directory.CreateDirectory(dir);
        _cachePath = cachePath ?? Path.Combine(dir, $"prices-{Sanitize(league)}.json");
        TryLoadCache();
    }

    /// <summary>Case-insensitive flat lookup. This is the highest observed variant.</summary>
    public float ValueChaos(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0f;
        return Volatile.Read(ref _byKey).TryGetValue(name.Trim(), out var value) ? value : 0f;
    }

    public IReadOnlyList<ItemPriceVariant> Variants(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Array.Empty<ItemPriceVariant>();
        return Volatile.Read(ref _variantsByName).TryGetValue(name.Trim(), out var rows)
            ? rows
            : Array.Empty<ItemPriceVariant>();
    }

    /// <summary>Quote the exact level/quality gem row. Unknown variants fail explicitly.</summary>
    public PriceQuote QuoteGem(string name, int level, int quality, bool? corrupted = null)
    {
        var rows = Variants(name)
            .Where(x => x.Category.Equals("SkillGem", StringComparison.OrdinalIgnoreCase)
                && (x.GemLevel ?? 1) == level
                && (x.GemQuality ?? 0) == quality);
        if (corrupted is not null)
            rows = rows.Where(x => x.Corrupted is null || x.Corrupted == corrupted);
        return Quote(rows, $"gem {name} L{level}/Q{quality}");
    }

    /// <summary>
    /// Quote a cluster jewel by its enchant text, passive count, and item-level price band.
    /// poe.ninja's levelRequired is the lower edge of the band; choose the highest edge that
    /// does not exceed the actual item level.
    /// </summary>
    public PriceQuote QuoteCluster(string enchantName, int passiveCount, int itemLevel)
    {
        var rows = Variants(enchantName)
            .Where(x => x.Category.Equals("ClusterJewel", StringComparison.OrdinalIgnoreCase)
                && ParsePassiveCount(x.Variant) == passiveCount
                && (x.LevelRequired ?? 0) <= itemLevel)
            .ToArray();
        if (rows.Length == 0)
            return PriceQuote.Unknown($"cluster {enchantName}/{passiveCount}/ilvl {itemLevel}");
        var band = rows.Max(x => x.LevelRequired ?? 0);
        return Quote(rows.Where(x => (x.LevelRequired ?? 0) == band),
            $"cluster {enchantName}/{passiveCount}/ilvl {itemLevel} (band {band})");
    }

    /// <summary>
    /// Quote a unique variant. For Voices, pass 1/3/5/7 to avoid treating the million-chaos
    /// one-passive row as the price of every Voices. Unknown rolls return a conservative range.
    /// </summary>
    public PriceQuote QuoteUnique(string name, int? passiveCount = null)
    {
        var rows = Variants(name).Where(x => x.Category.StartsWith("Unique", StringComparison.OrdinalIgnoreCase));
        if (passiveCount is not null)
        {
            rows = passiveCount == 1
                ? rows.Where(x => string.IsNullOrWhiteSpace(x.Variant) || ParsePassiveCount(x.Variant) == 1)
                : rows.Where(x => ParsePassiveCount(x.Variant) == passiveCount);
        }
        return Quote(rows, passiveCount is null ? $"unique {name} (roll unknown)" : $"unique {name}/{passiveCount} passives");
    }

    public bool NeedsRefresh => EntryCount == 0 || (DateTime.UtcNow - LastRefreshedAt) >= _refreshEvery;

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var flat = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            var variants = new List<ItemPriceVariant>();
            var failed = new List<string>();
            foreach (var category in ExchangeEndpoints)
                if (!await FetchExchangeAsync(category, flat, ct).ConfigureAwait(false))
                    failed.Add($"exchange:{category}");
            foreach (var category in ItemEndpoints)
                if (!await FetchItemsAsync(category, flat, variants, ct).ConfigureAwait(false))
                    failed.Add($"item:{category}");

            // Never replace a previously-good cache with a partial response. A 404 or schema
            // change in one category is otherwise indistinguishable from that entire market
            // having no value, which can corrupt both filtering and profit reports.
            if (failed.Count > 0)
                throw new InvalidDataException($"poe.ninja category refresh failed: {string.Join(", ", failed)}");
            if (flat.Count < 100 || variants.Count < 100)
                throw new InvalidDataException($"poe.ninja response incomplete: {flat.Count} names / {variants.Count} variants");

            var grouped = variants
                .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
            Interlocked.Exchange(ref _byKey, flat);
            Interlocked.Exchange(ref _variantsByName, grouped);
            LastRefreshedAt = DateTime.UtcNow;
            LastError = null;
            SaveCache();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private async Task<bool> FetchExchangeAsync(
        string category, Dictionary<string, float> flat, CancellationToken ct)
    {
        var url = "https://poe.ninja/poe1/api/economy/exchange/current/overview"
            + $"?league={Uri.EscapeDataString(_league)}&type={category}";
        using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return false;
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("lines", out var lines)
            || !doc.RootElement.TryGetProperty("items", out var items)) return false;

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.EnumerateArray())
        {
            var id = String(item, "id");
            var name = String(item, "name");
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name)) names[id] = name;
        }
        foreach (var line in lines.EnumerateArray())
        {
            var id = String(line, "id");
            var value = Single(line, "primaryValue");
            if (!string.IsNullOrEmpty(id) && names.TryGetValue(id, out var name)) AddMax(flat, name, value);
        }
        return names.Count > 0;
    }

    private async Task<bool> FetchItemsAsync(
        string category,
        Dictionary<string, float> flat,
        List<ItemPriceVariant> variants,
        CancellationToken ct)
    {
        var url = "https://poe.ninja/poe1/api/economy/stash/current/item/overview"
            + $"?league={Uri.EscapeDataString(_league)}&type={category}";
        using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return false;
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("lines", out var lines)) return false;
        var before = variants.Count;
        foreach (var line in lines.EnumerateArray())
        {
            var name = String(line, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var chaos = Single(line, "chaosValue");
            AddMax(flat, name, chaos);
            variants.Add(new ItemPriceVariant(
                name,
                category,
                chaos,
                String(line, "variant"),
                NullableInt(line, "gemLevel"),
                NullableInt(line, "gemQuality"),
                NullableInt(line, "levelRequired"),
                NullableBool(line, "corrupted"),
                String(line, "detailsId"),
                NullableInt(line, "listingCount") ?? NullableInt(line, "count") ?? 0));
        }
        return variants.Count > before;
    }

    private static PriceQuote Quote(IEnumerable<ItemPriceVariant> source, string match)
    {
        var rows = source.Where(x => x.ChaosValue > 0).ToArray();
        if (rows.Length == 0) return PriceQuote.Unknown(match);
        var min = rows.Min(x => x.ChaosValue);
        var max = rows.Max(x => x.ChaosValue);
        return new PriceQuote(min, min, max, rows.Length, match);
    }

    private static int ParsePassiveCount(string? variant)
    {
        if (string.IsNullOrWhiteSpace(variant)) return 0;
        var first = variant.AsSpan().TrimStart();
        var end = 0;
        while (end < first.Length && char.IsDigit(first[end])) end++;
        return end > 0 && int.TryParse(first[..end], out var value) ? value : 0;
    }

    private static string? String(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static float Single(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetSingle()
            : 0f;

    private static int? NullableInt(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    private static bool? NullableBool(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static void AddMax(Dictionary<string, float> sink, string name, float chaos)
    {
        if (sink.TryGetValue(name, out var existing) && existing >= chaos) return;
        sink[name] = chaos;
    }

    private void TryLoadCache()
    {
        if (!File.Exists(_cachePath)) return;
        try
        {
            var snapshot = JsonSerializer.Deserialize<CacheSnapshot>(File.ReadAllText(_cachePath));
            if (snapshot?.SchemaVersion != CacheSchemaVersion || snapshot.Entries is null) return;
            _byKey = new Dictionary<string, float>(snapshot.Entries, StringComparer.OrdinalIgnoreCase);
            if (snapshot.Variants is { Count: > 0 })
            {
                _variantsByName = snapshot.Variants
                    .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
            }
            LastRefreshedAt = snapshot.RefreshedAt;
        }
        catch { /* corrupt or legacy cache -> refresh */ }
    }

    private void SaveCache()
    {
        try
        {
            var snapshot = new CacheSnapshot
            {
                SchemaVersion = CacheSchemaVersion,
                RefreshedAt = LastRefreshedAt,
                Entries = Volatile.Read(ref _byKey),
                Variants = Volatile.Read(ref _variantsByName).Values.SelectMany(x => x).ToList(),
            };
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(snapshot));
        }
        catch { /* best effort */ }
    }

    private static string Sanitize(string value)
    {
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++) if (!char.IsLetterOrDigit(chars[i])) chars[i] = '_';
        return new string(chars);
    }

    private sealed class CacheSnapshot
    {
        public int SchemaVersion { get; set; }
        public DateTime RefreshedAt { get; set; }
        public Dictionary<string, float>? Entries { get; set; }
        public List<ItemPriceVariant>? Variants { get; set; }
    }
}
