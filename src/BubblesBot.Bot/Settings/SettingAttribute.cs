namespace BubblesBot.Bot.Settings;

/// <summary>
/// Annotates a property in <see cref="BotSettings"/> with the metadata the web UI needs to
/// render it. The schema endpoint walks all annotated properties so adding a setting later is
/// a one-property change — UI follows automatically.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SettingAttribute : Attribute
{
    public string Category { get; }
    public string DisplayName { get; }
    public string Description { get; }

    public SettingAttribute(string category, string displayName, string description = "")
    {
        Category    = category;
        DisplayName = displayName;
        Description = description;
    }
}

/// <summary>Optional numeric range for sliders / validation.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SettingRangeAttribute : Attribute
{
    public double Min  { get; }
    public double Max  { get; }
    public double Step { get; }
    public SettingRangeAttribute(double min, double max, double step = 1.0)
    {
        Min = min; Max = max; Step = step;
    }
}

/// <summary>Marks a property as a Win32 virtual-key code (rendered as a key-capture button).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SettingKeycodeAttribute : Attribute { }

/// <summary>Marks a property as a <c>SkillProfile</c> — rendered as a variable-length skill-binding list.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SettingSkillsAttribute : Attribute { }

/// <summary>Marks a property as a <c>FlaskProfile</c> — rendered as a flask-slot list.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SettingFlasksAttribute : Attribute { }

/// <summary>
/// Marks a property as a <em>nested settings object</em> — the web-UI schema walker recurses
/// into the target class and renders its [Setting]-annotated members inline, grouped under
/// the parent's display name. The property's own <see cref="SettingAttribute"/> is the
/// section header.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SettingNestedAttribute : Attribute { }

/// <summary>
/// Marks a property as a <c>List&lt;string&gt;</c> — rendered as a row-based add/remove list
/// in the web UI. Persisted as a JSON array of strings. Use for must-loot allowlists,
/// implicit-mod whitelists, ignore-name lists, etc.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SettingStringListAttribute : Attribute
{
    public string Placeholder { get; }
    public SettingStringListAttribute(string placeholder = "Add entry…") { Placeholder = placeholder; }
}

/// <summary>
/// Marks a <c>List&lt;string&gt;</c> property as the Ultimatum-modifier-danger override
/// table. The web UI renders a row per known mod (from <c>UltimatumModDanger.KnownMods</c>)
/// with a tier dropdown (Free/Easy/Medium/Hard/Very Hard/NEVER). User changes serialize
/// back into the underlying list as <c>"ModId=N"</c> entries — only mods whose tier
/// differs from the default get stored.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SettingModTableAttribute : Attribute { }

/// <summary>Renders semantic map modifiers with Allow/Avoid/Never build-policy choices.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SettingMapModifierTableAttribute : Attribute { }

/// <summary>
/// Renders the property as a dropdown/select. Pass paired values: index 0 = label, index 1 =
/// integer value, repeating. Example: <c>[SettingOptions("Loot", "0", "Waypoint test", "1")]</c>.
/// The web UI binds the integer values; labels are display-only.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SettingOptionsAttribute : Attribute
{
    public IReadOnlyList<(string Label, int Value)> Options { get; }
    public SettingOptionsAttribute(params string[] labelValuePairs)
    {
        if (labelValuePairs.Length % 2 != 0)
            throw new ArgumentException("SettingOptions requires pairs of label,value");
        var list = new List<(string, int)>(labelValuePairs.Length / 2);
        for (var i = 0; i < labelValuePairs.Length; i += 2)
            list.Add((labelValuePairs[i], int.Parse(labelValuePairs[i + 1])));
        Options = list;
    }
}
