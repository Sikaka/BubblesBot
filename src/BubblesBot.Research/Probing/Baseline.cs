using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BubblesBot.Research.Probing;

/// <summary>
/// The gitignored ground-truth facts the probes validate against — your independent authority.
/// Captured per-patch from the live game (by hand, or auto-filled from the oracle). Stored as
/// flat string key/value pairs so a probe just asks <c>ctx.Facts.TryGetInt("character.hp", ...)</c>.
///
/// <para>Not committed: facts are specific to your character/account/patch and would be noise in
/// git. The durable artifact is <c>KnownOffsets.cs</c>, not this file.</para>
///
/// <para>Path resolution: <c>BUBBLES_BASELINE</c> env var if set, else
/// <c>%LOCALAPPDATA%/BubblesBot/research/baseline.local.json</c>. The resolved path is printed on
/// every run.</para>
/// </summary>
public sealed class Baseline
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Path { get; }
    public string? CapturedUtc { get; set; }
    public string? Note { get; set; }
    public Dictionary<string, string> Facts { get; set; } = new(StringComparer.Ordinal);

    private Baseline(string path) => Path = path;

    public static string ResolvePath()
    {
        var env = Environment.GetEnvironmentVariable("BUBBLES_BASELINE");
        return string.IsNullOrWhiteSpace(env)
            ? System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BubblesBot", "research", "baseline.local.json")
            : env;
    }

    public static Baseline Load(string? path = null)
    {
        path ??= ResolvePath();
        var b = new Baseline(path);
        if (!File.Exists(path)) return b;
        try
        {
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(path), JsonOpts);
            if (dto is not null)
            {
                b.CapturedUtc = dto.CapturedUtc;
                b.Note = dto.Note;
                if (dto.Facts is not null)
                    b.Facts = new Dictionary<string, string>(dto.Facts, StringComparer.Ordinal);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WARN: could not parse baseline '{path}': {ex.Message}");
        }
        return b;
    }

    public void Save()
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var dto = new Dto { CapturedUtc = CapturedUtc, Note = Note, Facts = Facts };
        File.WriteAllText(Path, JsonSerializer.Serialize(dto, JsonOpts));
    }

    public bool Has(string key) => Facts.ContainsKey(key);
    public void Set(string key, string value) => Facts[key] = value;

    public bool TryGetStr(string key, out string value)
    {
        if (Facts.TryGetValue(key, out var v) && v.Length > 0) { value = v; return true; }
        value = "";
        return false;
    }

    public bool TryGetInt(string key, out int value)
    {
        value = 0;
        return Facts.TryGetValue(key, out var v)
            && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    public bool TryGetLong(string key, out long value)
    {
        value = 0;
        return Facts.TryGetValue(key, out var v)
            && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    public bool TryGetFloat(string key, out float value)
    {
        value = 0;
        return Facts.TryGetValue(key, out var v)
            && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private sealed class Dto
    {
        public string? CapturedUtc { get; set; }
        public string? Note { get; set; }
        public Dictionary<string, string>? Facts { get; set; }
    }
}
