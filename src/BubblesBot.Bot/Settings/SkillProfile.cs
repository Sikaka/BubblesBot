namespace BubblesBot.Bot.Settings;

/// <summary>
/// What a skill slot is for. Drives whether navigation/combat/buff systems consider this slot
/// at all and how they aim it.
/// </summary>
public enum SkillRole
{
    /// <summary>Slot exists but is never fired. Use to keep a binding documented but inactive.</summary>
    Disabled,
    /// <summary>The held movement skill (Run, Walk, Shield Charge, Whirling Blades). Held while traveling.</summary>
    Walk,
    /// <summary>Tap-to-fire dash (Flame Dash, Frostblink, Lightning Warp). Used by navigation for gap crossing.</summary>
    Dash,
    /// <summary>Single-target / AOE attack. Used by combat against enemies.</summary>
    Attack,
    /// <summary>Self-buff or warcry. Cast with cursor parked on player.</summary>
    SelfBuff,
    /// <summary>Channelled skill (Cyclone, Incinerate). Held while predicate true.</summary>
    Channel,
    /// <summary>Passive/aura skill — usually toggled once at start. v1 just keeps the slot on file.</summary>
    Aura,
    /// <summary>
    /// Curse/mark cast AT an enemy (Penance Mark, Assassin's Mark, a targeted curse). Combat
    /// casts it on cooldown at the highest-priority rare+ target to shape the fight (Penance
    /// Mark spawns phantasms near the cursed enemy). Appended last so its ordinal (7) stays
    /// stable — roles serialize as ints in config.json.
    /// </summary>
    Mark,
}

/// <summary>
/// One configured skill binding. Variable count per profile — the user adds as many as their
/// build needs. <see cref="Vk"/> uses Win32 virtual-key codes including mouse buttons
/// (0x01 LBUTTON, 0x02 RBUTTON, 0x04 MBUTTON, 0x05 XBUTTON1, 0x06 XBUTTON2). The bot routes
/// these through mouse INPUT events instead of keyboard so PoE sees the same event a real
/// click would generate.
///
/// <para><b>Charges and cooldowns.</b> v1 uses <see cref="ChargeCount"/> + <see cref="ChargeRechargeMs"/>
/// as a static model — A* asks "how many blinks can I afford right now?" and SkillBook answers
/// from local last-cast tracking. Real per-skill cooldown reads from PoE memory will replace
/// this when validated; the data shape stays the same.</para>
/// </summary>
public sealed class SkillSlot
{
    /// <summary>Human label for the UI ("Flame Dash", "Vaal Cyclone"). Display only.</summary>
    public string Name { get; set; } = "";

    /// <summary>Win32 virtual-key code, including mouse buttons (0x01-0x06).</summary>
    public int Vk { get; set; }

    public SkillRole Role { get; set; } = SkillRole.Disabled;

    /// <summary>
    /// Dash only: this skill can cross terrain that walk-skills can't (gaps where
    /// pathfinding=0 but targeting&gt;0). Without a Dash slot tagged true, navigation will
    /// route around all gaps.
    /// </summary>
    public bool CanCrossGaps { get; set; }

    /// <summary>
    /// Floor on consecutive activations of this slot. Soft cooldown — even if the real skill
    /// has no in-game cooldown, this prevents the bot from spamming faster than PoE can react.
    /// 0 = no floor. For dashes with charges this is the recharge rate per charge.
    /// </summary>
    public int MinCastIntervalMs { get; set; } = 100;

    /// <summary>Effective range in grid cells. Used by combat (max attack range) and dash (max blink range).</summary>
    public int MaxRangeGrid { get; set; } = 30;

    /// <summary>How many uses are stockpiled when fully ready. Most dashes have 1-3 charges.</summary>
    public int ChargeCount { get; set; } = 1;

    /// <summary>Milliseconds to recover one charge. Combined with ChargeCount this gives the steady-state cast rate.</summary>
    public int ChargeRechargeMs { get; set; } = 3000;

    /// <summary>
    /// PoE gem id (UInt16). When set, the bot reads real cooldown state from
    /// <c>ActorSkillsCooldowns</c> instead of relying on client-side simulation. Find a
    /// skill's gem id via <c>ServerData.SkillBarIds</c> on the dashboard or via POEMCP
    /// (<c>Player.GetComponent&lt;Actor&gt;().ActorSkills.First(s =&gt; s.Name == "X").Id</c>).
    /// 0 = simulation only.
    /// </summary>
    public ushort GemId { get; set; }

    // ── Timing model (declarative build knobs; user-tuned, refined from AnimationController later) ──
    // These describe HOW a skill occupies the character in time, so movement/dodge/arbiter logic
    // can reason about it instead of firing blind. All default to "instant, no lock" so existing
    // tap-attack behavior is unchanged until a slot opts in.

    /// <summary>
    /// Windup before the skill's effect lands (ms). Informational for now; lets combat avoid
    /// re-issuing the same cast mid-windup. 0 = instant.
    /// </summary>
    public int CastTimeMs { get; set; }

    /// <summary>
    /// How long the character is committed / unable to move after firing (ms). This is the
    /// action-lock: attacks and non-movement casts that root you set this so movement stops
    /// fighting the animation. 0 = no lock (default — preserves drive-by tap behavior).
    /// Often ≈ attack/cast time; source from config, refine from AnimationController reads.
    /// </summary>
    public int LockMs { get; set; }

    /// <summary>
    /// Dash only: PoE refuses (or short-fires) a blink below this grid distance. The blink
    /// dispatcher must not fire under it — walk the remainder instead. 0 = no minimum.
    /// Frostblink's floor is the canonical case.
    /// </summary>
    public int MinCastDistanceGrid { get; set; }

    /// <summary>
    /// Safe to tap without releasing the walk hold (buffs/curses that don't root). When false,
    /// the arbiter/movement layer treats firing this as movement-interrupting. Default false
    /// (conservative). Attack slots stay drive-by via their own path, not this flag.
    /// </summary>
    public bool CastableWhileMoving { get; set; }

    /// <summary>
    /// Attack role: hold the key to auto-repeat the attack (cursor retargeted each tick) instead
    /// of tapping once per tick. For builds whose primary damage is "hold to attack". Default
    /// false = the existing per-tick tap.
    /// </summary>
    public bool HoldToRepeat { get; set; }

    /// <summary>
    /// Channel role: this channelled skill IS a movement source (Cyclone, Righteous Fire while
    /// moving). When true, the mode can drive travel by holding this skill aimed at the path
    /// node, via the cursor arbiter, instead of the Walk key. Default false = stationary channel.
    /// </summary>
    public bool IsMovementChannel { get; set; }
}

/// <summary>
/// Top-level skill configuration owned by <see cref="BotSettings"/>. Variable-length list so
/// users with extra binds (mouse-button-bound utilities, multiple flask slots, more than 5
/// active skills) don't have to fight a fixed array. Iteration order is the binding order
/// the user added — combat priority comes from <see cref="SkillSlot.Role"/> and
/// position-in-list as a tiebreaker.
/// </summary>
public sealed class SkillProfile
{
    public List<SkillSlot> Slots { get; set; } = new()
    {
        // Out-of-the-box defaults: E = walk, R = Flame Dash (gap-crosser).
        new SkillSlot { Name = "Walk",       Vk = 0x45, Role = SkillRole.Walk,                                                         MaxRangeGrid = 200 },
        new SkillSlot { Name = "Flame Dash", Vk = 0x52, Role = SkillRole.Dash,    CanCrossGaps = true, MinCastIntervalMs = 350,        MaxRangeGrid = 35, ChargeCount = 3, ChargeRechargeMs = 3300, GemId = 0 },
    };

    /// <summary>The walk slot (first one tagged Walk). Returns null if the user disabled all walk skills.</summary>
    public SkillSlot? WalkSlot
    {
        get
        {
            foreach (var s in Slots) if (s.Role == SkillRole.Walk) return s;
            return null;
        }
    }

    /// <summary>All gap-crossing dashes, in declared order. Empty when no dash with CanCrossGaps is bound.</summary>
    public IEnumerable<SkillSlot> GapCrossers
    {
        get
        {
            foreach (var s in Slots)
                if (s.Role == SkillRole.Dash && s.CanCrossGaps && s.Vk != 0) yield return s;
        }
    }

    /// <summary>All bound slots of a given role, in declared order (priority = position).</summary>
    public IEnumerable<SkillSlot> OfRole(SkillRole role)
    {
        foreach (var s in Slots)
            if (s.Role == role && s.Vk != 0) yield return s;
    }

    /// <summary>First bound Attack slot, else null. The primary damage picker's default.</summary>
    public SkillSlot? PrimaryAttack
    {
        get { foreach (var s in Slots) if (s.Role == SkillRole.Attack && s.Vk != 0) return s; return null; }
    }

    /// <summary>First bound Mark/curse slot (Penance Mark etc.), else null.</summary>
    public SkillSlot? PrimaryMark
    {
        get { foreach (var s in Slots) if (s.Role == SkillRole.Mark && s.Vk != 0) return s; return null; }
    }

    /// <summary>First bound movement-channel slot (Channel role + IsMovementChannel), else null.</summary>
    public SkillSlot? MovementChannel
    {
        get { foreach (var s in Slots) if (s.Role == SkillRole.Channel && s.IsMovementChannel && s.Vk != 0) return s; return null; }
    }
}
