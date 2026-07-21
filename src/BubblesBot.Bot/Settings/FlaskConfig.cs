namespace BubblesBot.Bot.Settings;

/// <summary>What triggers a flask press.</summary>
public enum FlaskTrigger
{
    /// <summary>Disabled — never auto-fire this slot.</summary>
    Disabled,
    /// <summary>Fire when player HP fraction drops below <see cref="FlaskSlot.HpThreshold"/>.</summary>
    Hp,
    /// <summary>Fire when player Mana fraction drops below <see cref="FlaskSlot.ManaThreshold"/>.</summary>
    Mana,
    /// <summary>Fire on a fixed time interval (utility flasks: Quicksilver, Granite, Jade).</summary>
    Time,
    /// <summary>Fire when the named buff is missing or expiring (e.g. "flask_effect_diamond").</summary>
    BuffMissing,
}

/// <summary>One configured flask slot. Bind hotkey + trigger + threshold/interval.</summary>
public sealed class FlaskSlot
{
    public string  Name { get; set; } = "";
    public int     Vk { get; set; }                       // typically 0x31..0x35 (1..5)
    public FlaskTrigger Trigger { get; set; } = FlaskTrigger.Disabled;
    public float   HpThreshold { get; set; } = 0.6f;      // 0.0-1.0 fraction
    public float   ManaThreshold { get; set; } = 0.3f;
    public int     IntervalMs { get; set; } = 5000;       // for Time trigger
    public string  BuffName { get; set; } = "";           // for BuffMissing trigger
    public int     CooldownMs { get; set; } = 4500;       // floor between consecutive presses (flask charge regen)
}

/// <summary>
/// Variable-length flask config — same shape as <see cref="SkillProfile"/>. Defaults
/// give a starter set: slot 1 = HP flask @ 60%, slot 2 = mana flask @ 30%, slots 3-5 disabled.
/// </summary>
public sealed class FlaskProfile
{
    public List<FlaskSlot> Slots { get; set; } = new()
    {
        new FlaskSlot { Name = "Life flask",    Vk = 0x31, Trigger = FlaskTrigger.Hp,    HpThreshold = 0.6f },
        new FlaskSlot { Name = "Mana flask",    Vk = 0x32, Trigger = FlaskTrigger.Mana,  ManaThreshold = 0.4f, CooldownMs = 2000 },
        new FlaskSlot { Name = "Utility 3",     Vk = 0x33 },
        new FlaskSlot { Name = "Utility 4",     Vk = 0x34 },
        new FlaskSlot { Name = "Utility 5",     Vk = 0x35 },
    };
}
