namespace BubblesBot.Bot.Settings;

/// <summary>
/// Per-character settings file. Wraps <see cref="SettingsStore"/> with character-name-keyed
/// load/save. The "active" character drives which config file <see cref="SettingsStore"/>
/// targets — when the bot detects a character change (login screen → character select →
/// world enter), <see cref="SwitchTo"/> reloads.
///
/// <para>Files live at <c>%APPDATA%/BubblesBot/profiles/{name}.json</c>. The filesystem-safe
/// version of the character name is used; collisions across characters with sanitization-
/// equivalent names are theoretically possible but in practice extremely unlikely.</para>
///
/// <para><b>Default profile.</b> When the character name is empty (login screen, can't read
/// player), we fall back to <c>config.json</c> in the BubblesBot dir — same path
/// <see cref="SettingsStore"/> uses standalone. This keeps the bot usable before the player
/// has logged into a character.</para>
/// </summary>
public sealed class ProfileStore
{
    private readonly SettingsStore _store;
    public  string ActiveProfile { get; private set; } = string.Empty;

    public ProfileStore(SettingsStore store) { _store = store; }

    /// <summary>
    /// Switch active profile to <paramref name="characterName"/>. No-op if already active.
    /// Triggers a reload from disk (or creates a fresh defaulted profile).
    /// </summary>
    public bool SwitchTo(string characterName)
    {
        var key = Sanitize(characterName);
        if (key == ActiveProfile) return false;
        ActiveProfile = key;
        var path = string.IsNullOrEmpty(key) ? DefaultPath() : ProfilePath(characterName);
        _store.RebindPath(path);
        return true;
    }

    private static string DefaultPath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BubblesBot");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "config.json");
    }

    public static string ProfilePath(string characterName)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BubblesBot", "profiles");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{Sanitize(characterName)}.json");
    }

    private static string Sanitize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '_' && chars[i] != '-') chars[i] = '_';
        return new string(chars);
    }
}
