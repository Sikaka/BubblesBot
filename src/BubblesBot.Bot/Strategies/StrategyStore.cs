namespace BubblesBot.Bot.Strategies;

/// <summary>Outcome of an import: the stored strategy on success, null plus errors on rejection.</summary>
public sealed record StrategyImportResult(FarmingStrategy? Strategy, StrategyValidationResult Validation);

/// <summary>
/// The strategy library: id-keyed files in <c>%APPDATA%/BubblesBot/strategies/</c> plus the
/// atomically-published active snapshot the runtime reads. Mirrors <c>SettingsStore</c>'s
/// contract (immutable published snapshot, <see cref="Version"/> counter, <see cref="Changed"/>
/// event, atomic tmp+move writes) so modes consume it the same way they consume settings.
///
/// <para>Fail-closed rules: unparseable files are skipped and reported, never half-loaded;
/// imports are rejected (not stored) on validation errors; saving the active strategy into an
/// invalid state deactivates it — the runtime refuses to arm without a valid active strategy
/// rather than falling back to defaults.</para>
/// </summary>
public sealed class StrategyStore
{
    private readonly string _directory;
    private readonly object _lock = new();
    private readonly Dictionary<string, FarmingStrategy> _docsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _jsonById = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _loadErrors = new();

    private volatile FarmingStrategy? _active;
    private long _version;

    /// <summary>Atomically-published active strategy, or null when none is valid/selected. Treat as immutable.</summary>
    public FarmingStrategy? Active => _active;

    public string? ActiveId => _active?.Identity.Id;

    /// <summary>Increments whenever the library or the active snapshot changes.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>Files that failed to parse at load time, with reasons. Surfaced for diagnostics; never fatal.</summary>
    public IReadOnlyList<string> LoadErrors { get { lock (_lock) return _loadErrors.ToArray(); } }

    public int Count { get { lock (_lock) return _docsById.Count; } }

    /// <summary>Fires after each library/active change. UI subscribers re-render; mode code re-reads next tick.</summary>
    public event Action? Changed;

    public StrategyStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BubblesBot", "strategies");
        Directory.CreateDirectory(_directory);
        LoadAll();
    }

    /// <summary>
    /// Save the given strategies only if the library is currently empty. Idempotent across
    /// restarts and milestones — the one-time seed migration and any UI-created strategy both
    /// leave the library non-empty, so this never overwrites user content.
    /// </summary>
    public void SeedIfEmpty(IEnumerable<FarmingStrategy> seeds)
    {
        lock (_lock)
        {
            if (_docsById.Count > 0) return;
        }
        foreach (var seed in seeds) Save(seed);
    }

    public IReadOnlyList<FarmingStrategy> List()
    {
        lock (_lock)
            return _docsById.Values
                .OrderBy(doc => doc.Identity.Name, StringComparer.OrdinalIgnoreCase)
                .Select(StrategySerialization.Clone)
                .ToArray();
    }

    public bool TryGet(string id, out FarmingStrategy strategy)
    {
        lock (_lock)
        {
            if (_docsById.TryGetValue(id, out var doc))
            {
                strategy = StrategySerialization.Clone(doc);
                return true;
            }
        }
        strategy = null!;
        return false;
    }

    /// <summary>
    /// Persist a strategy (create or update). Drafts with validation errors ARE saved — the
    /// editor must be able to keep work in progress — but an invalid save that targets the
    /// active strategy deactivates it. Returns the validation result so callers can surface it.
    /// </summary>
    public StrategyValidationResult Save(FarmingStrategy strategy)
    {
        if (string.IsNullOrWhiteSpace(strategy.Identity.Id))
            strategy.Identity.Id = StrategyIdentity.NewId();
        if (strategy.Identity.CreatedUtc == default)
            strategy.Identity.CreatedUtc = DateTime.UtcNow;
        strategy.Identity.ModifiedUtc = DateTime.UtcNow;

        var validation = StrategyValidator.Validate(strategy);
        lock (_lock)
        {
            WriteToDisk(strategy);
            if (string.Equals(ActiveId, strategy.Identity.Id, StringComparison.OrdinalIgnoreCase))
            {
                if (validation.Ok)
                    _active = StrategySerialization.Clone(strategy);
                else
                {
                    _active = null;
                    validation.Warn("the active strategy was deactivated because it no longer validates");
                }
            }
            Interlocked.Increment(ref _version);
        }
        Changed?.Invoke();
        return validation;
    }

    /// <summary>Delete a stored strategy. The active strategy cannot be deleted (deactivate first).</summary>
    public void Delete(string id)
    {
        lock (_lock)
        {
            if (string.Equals(ActiveId, id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("cannot delete the active strategy; deactivate it first");
            if (!_docsById.Remove(id)) return;
            _jsonById.Remove(id);
            try
            {
                var path = PathFor(id);
                if (File.Exists(path)) File.Delete(path);
                var temp = path + ".tmp";
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch
            {
                // A stale file resurfaces on next load; the in-memory removal already happened
                // and a later Save/Delete retries the disk state.
            }
            Interlocked.Increment(ref _version);
        }
        Changed?.Invoke();
    }

    /// <summary>Re-validate and publish a stored strategy as active. Errors leave the current active untouched.</summary>
    public StrategyValidationResult Activate(string id)
    {
        StrategyValidationResult validation;
        lock (_lock)
        {
            if (!_docsById.TryGetValue(id, out var doc))
            {
                validation = new StrategyValidationResult();
                validation.Error($"unknown strategy id '{id}'");
                return validation;
            }
            validation = StrategyValidator.Validate(doc);
            if (!validation.Ok) return validation;
            _active = StrategySerialization.Clone(doc);
            Interlocked.Increment(ref _version);
        }
        Changed?.Invoke();
        return validation;
    }

    public void Deactivate()
    {
        lock (_lock)
        {
            if (_active is null) return;
            _active = null;
            Interlocked.Increment(ref _version);
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Import a shared file. Fail-closed: parse defects and validation errors reject the
    /// document without storing anything. A colliding or missing id is regenerated so an
    /// import can never overwrite an existing strategy. Imports are always stored inactive.
    /// </summary>
    public StrategyImportResult Import(string json)
    {
        FarmingStrategy doc;
        try
        {
            doc = StrategySerialization.Parse(json);
        }
        catch (StrategyFormatException ex)
        {
            var failed = new StrategyValidationResult();
            failed.Error(ex.Message);
            return new StrategyImportResult(null, failed);
        }

        var validation = StrategyValidator.Validate(doc);
        if (!validation.Ok) return new StrategyImportResult(null, validation);

        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(doc.Identity.Id) || _docsById.ContainsKey(doc.Identity.Id))
                doc.Identity.Id = StrategyIdentity.NewId();
            doc.Identity.ModifiedUtc = DateTime.UtcNow;
            if (doc.Identity.CreatedUtc == default) doc.Identity.CreatedUtc = doc.Identity.ModifiedUtc;
            WriteToDisk(doc);
            Interlocked.Increment(ref _version);
        }
        Changed?.Invoke();
        return new StrategyImportResult(StrategySerialization.Clone(doc), validation);
    }

    /// <summary>The stored file content verbatim — the file IS the interchange format.</summary>
    public string Export(string id)
    {
        lock (_lock)
        {
            if (_jsonById.TryGetValue(id, out var json)) return json;
        }
        throw new KeyNotFoundException($"unknown strategy id '{id}'");
    }

    private void LoadAll()
    {
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(path);
                var doc = StrategySerialization.Parse(json);
                if (string.IsNullOrWhiteSpace(doc.Identity.Id))
                {
                    _loadErrors.Add($"{Path.GetFileName(path)}: identity.id is empty");
                    continue;
                }
                _docsById[doc.Identity.Id] = doc;
                _jsonById[doc.Identity.Id] = json;
            }
            catch (Exception ex) when (ex is StrategyFormatException or IOException)
            {
                _loadErrors.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }
    }

    private void WriteToDisk(FarmingStrategy strategy)
    {
        var stored = StrategySerialization.Clone(strategy);
        var json = StrategySerialization.Serialize(stored);
        var path = PathFor(stored.Identity.Id);
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
        _docsById[stored.Identity.Id] = stored;
        _jsonById[stored.Identity.Id] = json;
    }

    private string PathFor(string id) => Path.Combine(_directory, id + ".json");
}
