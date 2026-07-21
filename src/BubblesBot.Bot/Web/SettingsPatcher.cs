using System.Reflection;
using System.Text.Json;
using BubblesBot.Bot.Settings;

namespace BubblesBot.Bot.Web;

/// <summary>
/// Path-targeted settings writes for <c>PATCH /api/settings</c>. Fixes the whole-object PUT
/// footgun where a stale or partial body silently resets omitted fields to class defaults:
/// a patch touches exactly the paths it names. Writable paths are whitelisted the same way
/// the schema walker exposes them — [Setting]-annotated properties, recursing through
/// [SettingNested] — so a patch can never reach un-exposed state.
/// </summary>
public static class SettingsPatcher
{
    public sealed record PatchOp(string[] Path, JsonElement Value);

    /// <summary>
    /// Apply every op to <paramref name="target"/> in order. Returns errors for unknown or
    /// non-writable paths and unconvertible values; on any error the CALLER must discard the
    /// target (apply-to-clone, validate, then re-apply to the real object).
    /// </summary>
    public static List<SettingsValidationError> Apply(
        BotSettings target, IReadOnlyList<PatchOp> ops, JsonSerializerOptions jsonOptions)
    {
        var errors = new List<SettingsValidationError>();
        foreach (var op in ops)
        {
            var pathLabel = string.Join(".", op.Path);
            if (op.Path.Length == 0)
            {
                errors.Add(new SettingsValidationError("", "empty path"));
                continue;
            }

            object current = target;
            PropertyInfo? property = null;
            var resolved = true;
            for (var i = 0; i < op.Path.Length; i++)
            {
                property = FindSettingProperty(current.GetType(), op.Path[i]);
                if (property is null)
                {
                    errors.Add(new SettingsValidationError(pathLabel, $"unknown or non-writable path segment '{op.Path[i]}'"));
                    resolved = false;
                    break;
                }
                if (i < op.Path.Length - 1)
                {
                    if (property.GetCustomAttribute<SettingNestedAttribute>() is null
                        || property.GetValue(current) is not { } child)
                    {
                        errors.Add(new SettingsValidationError(pathLabel, $"'{op.Path[i]}' is not a nested settings object"));
                        resolved = false;
                        break;
                    }
                    current = child;
                }
            }
            if (!resolved || property is null) continue;

            try
            {
                var value = op.Value.Deserialize(property.PropertyType, jsonOptions);
                if (value is null && property.PropertyType.IsValueType && Nullable.GetUnderlyingType(property.PropertyType) is null)
                {
                    errors.Add(new SettingsValidationError(pathLabel, "null is not valid for this setting"));
                    continue;
                }
                property.SetValue(current, value);
            }
            catch (JsonException ex)
            {
                errors.Add(new SettingsValidationError(pathLabel, $"value does not fit this setting: {ex.Message}"));
            }
        }
        return errors;
    }

    private static PropertyInfo? FindSettingProperty(Type type, string jsonName)
    {
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || !prop.CanWrite && prop.GetCustomAttribute<SettingNestedAttribute>() is null) continue;
            if (prop.GetCustomAttribute<SettingAttribute>() is null) continue;
            var name = char.ToLower(prop.Name[0]) + prop.Name[1..];
            if (string.Equals(name, jsonName, StringComparison.Ordinal)) return prop;
        }
        return null;
    }
}
