using System.Reflection;
using BubblesBot.Bot.Settings;

namespace BubblesBot.Bot.Web;

public sealed record SettingsValidationError(string Path, string Message);

/// <summary>
/// Server-side enforcement of the setting attributes that were previously UI hints only:
/// [SettingRange] bounds, [SettingOptions] membership, and keycode range. Violations REJECT
/// the write (HTTP 422) — never clamp, because a silently clamped value and the user's intent
/// diverge without anyone noticing. Rules are reflected once per process.
/// </summary>
public static class SettingsValidator
{
    private sealed record Rule(string Path, Func<object, object?> Getter, Func<object?, string?> Check);

    private static readonly IReadOnlyList<Rule> Rules = BuildRules(typeof(BotSettings));

    public static List<SettingsValidationError> Validate(BotSettings settings)
    {
        var errors = new List<SettingsValidationError>();
        foreach (var rule in Rules)
        {
            var message = rule.Check(rule.Getter(settings));
            if (message is not null) errors.Add(new SettingsValidationError(rule.Path, message));
        }
        return errors;
    }

    private static List<Rule> BuildRules(Type rootType)
    {
        var rules = new List<Rule>();
        Walk(rootType, "", roots => roots, rules);
        return rules;
    }

    private static void Walk(Type type, string pathPrefix, Func<object, object?> resolve, List<Rule> rules)
    {
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetCustomAttribute<SettingAttribute>() is null) continue;
            var jsonName = char.ToLower(prop.Name[0]) + prop.Name[1..];
            var path = pathPrefix.Length == 0 ? jsonName : $"{pathPrefix}.{jsonName}";
            var localProp = prop;
            object? Get(object root) => resolve(root) is { } parent ? localProp.GetValue(parent) : null;

            if (prop.GetCustomAttribute<SettingNestedAttribute>() is not null)
            {
                Walk(prop.PropertyType, path, root => Get(root), rules);
                continue;
            }

            if (prop.GetCustomAttribute<SettingKeycodeAttribute>() is not null)
            {
                rules.Add(new Rule(path, Get, value =>
                    value is int vk && vk is >= 0 and <= 255 ? null : "keycode must be 0..255"));
                continue;
            }

            if (prop.GetCustomAttribute<SettingOptionsAttribute>() is { } options)
            {
                var legal = options.Options.Select(o => o.Value).ToHashSet();
                var labels = string.Join(", ", legal.OrderBy(v => v));
                rules.Add(new Rule(path, Get, value =>
                    value is int i && legal.Contains(i) ? null : $"value must be one of: {labels}"));
                continue;
            }

            if (prop.GetCustomAttribute<SettingRangeAttribute>() is { } range)
            {
                rules.Add(new Rule(path, Get, value =>
                {
                    double numeric = value switch
                    {
                        int i => i,
                        float f => f,
                        double d => d,
                        _ => double.NaN,
                    };
                    if (double.IsNaN(numeric)) return "expected a numeric value";
                    return numeric >= range.Min && numeric <= range.Max
                        ? null
                        : $"must be between {range.Min} and {range.Max}";
                }));
            }
        }
    }
}
