using System.Text.Json;

namespace BubblesBot.Bot.Settings;

/// <summary>
/// Loads and persists <see cref="BotSettings"/> to <c>%APPDATA%/BubblesBot/config.json</c>.
/// Save is debounced — multiple property changes in quick succession produce one disk write.
/// Thread-safe for the read side; writes serialize through a lock.
///
/// <para>The store IS the in-process source of truth — UI, hotkeys, and modes all read from
/// and write to <see cref="Current"/>.</para>
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private string _configPath;
    private readonly object _writeLock = new();
    private DateTime _saveDebounceUntil = DateTime.MinValue;
    private bool _dirty;

    private volatile BotSettings _current;
    private long _version;

    /// <summary>
    /// Atomically-published settings snapshot. Mutations clone before publishing so readers
    /// never observe a partially-updated profile.
    /// </summary>
    public BotSettings Current => _current;

    /// <summary>Increments whenever a new settings snapshot is published.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>Currently-bound config path. Changes when <see cref="RebindPath"/> is called.</summary>
    public string ConfigPath => _configPath;

    /// <summary>Fires after each in-memory change. UI subscribers re-render; mode code re-reads on next tick.</summary>
    public event Action? Changed;

    public SettingsStore()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "BubblesBot");
        Directory.CreateDirectory(dir);
        _configPath = Path.Combine(dir, "config.json");
        _current = Load();
    }

    /// <summary>Apply an update lambda and mark dirty. Triggers <see cref="Changed"/>.</summary>
    public void Mutate(Action<BotSettings> update)
    {
        lock (_writeLock)
        {
            var next = Clone(_current);
            update(next);
            Publish(next);
            MarkDirty();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Switch to a different on-disk config file. Flushes the current settings first (so
    /// pending edits aren't lost), then loads the target. If the target doesn't exist, a
    /// fresh-default <see cref="BotSettings"/> is loaded — which on first dirty mutation will
    /// create the file. Used by <see cref="ProfileStore"/> for per-character profiles.
    /// </summary>
    public void RebindPath(string newPath)
    {
        if (string.Equals(newPath, _configPath, StringComparison.OrdinalIgnoreCase)) return;
        lock (_writeLock)
        {
            if (_dirty) SaveNow();
            _configPath = newPath;
            Publish(Load());
            _dirty = false;
        }
        Changed?.Invoke();
    }

    /// <summary>Call once per tick. Flushes pending writes after a short debounce.</summary>
    public void Tick()
    {
        if (!_dirty) return;
        if (DateTime.UtcNow < _saveDebounceUntil) return;
        SaveNow();
    }

    private void MarkDirty()
    {
        _dirty = true;
        _saveDebounceUntil = DateTime.UtcNow.AddMilliseconds(250);
    }

    private void SaveNow()
    {
        BotSettings snapshot;
        lock (_writeLock)
        {
            snapshot = Clone(_current);
            _dirty = false;
        }
        try
        {
            var tmp = _configPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, JsonOptions));
            File.Move(tmp, _configPath, overwrite: true);
        }
        catch
        {
            // Don't crash the bot on a transient file lock; we'll retry next tick.
            _dirty = true;
        }
    }

    private BotSettings Load()
    {
        if (!File.Exists(_configPath)) return new BotSettings();
        try
        {
            var json = File.ReadAllText(_configPath);
            var loaded = JsonSerializer.Deserialize<BotSettings>(json, JsonOptions);
            var settings = loaded ?? new BotSettings();
            // Modes 8/9 were temporary loop/clear prototypes. Mode 4 owns both now.
            // Mode 6 is the production Simulacrum adapter (the old Ultimatum prototype is
            // retained as research code but is no longer dispatched).
            if (settings.ActiveMode is 8 or 9) settings.ActiveMode = 4;
            else if (settings.ActiveMode is not (0 or 4 or 5 or 6)) settings.ActiveMode = 0;
            return settings;
        }
        catch
        {
            return new BotSettings();
        }
    }

    private static BotSettings Clone(BotSettings s)
        => JsonSerializer.Deserialize<BotSettings>(JsonSerializer.Serialize(s, JsonOptions), JsonOptions)!;

    private void Publish(BotSettings next)
    {
        _current = next;
        Interlocked.Increment(ref _version);
    }
}
