using BubblesBot.Core.Game;
using BubblesBot.Core.Pathfinding;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Modes;

/// <summary>
/// Pure policy and observation state for the Cloister + Ritual stacked-deck preset. The
/// metadata identities were live-validated in AutoExile and are kept here instead of being
/// scattered through combat, stash, and map-device code.
/// </summary>
public static class StackedDeckPolicy
{
    public const string CloisterScarabPathFragment = "ScarabDivinationCardsNew1";
    public const string CloisterMonsterPathFragment = "DemonFemale";
    public const int DefaultCloisterScarabsPerMap = 5;
    public const float RitualCorpseRadiusGrid = 45f;
    public const double CloisterDensityWeight = 12d;

    public static bool IsCloisterScarab(string path)
        => path.Contains(CloisterScarabPathFragment, StringComparison.OrdinalIgnoreCase);

    public static bool IsCloisterMonster(string path)
        => path.Contains(CloisterMonsterPathFragment, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Select the next available Ritual. Cloister-corpse count is the primary key because
    /// the first altar's pool is repeated by every later altar. Travel distance only breaks
    /// equal-profit ties.
    /// </summary>
    public static MechanicEntry? ChooseRitual(
        IEnumerable<MechanicEntry> available,
        Vector2i player,
        Func<uint, int> corpseCount)
    {
        MechanicEntry? best = null;
        var bestCorpses = -1;
        long bestDistance2 = long.MaxValue;
        foreach (var altar in available)
        {
            var corpses = corpseCount(altar.Id);
            long dx = altar.GridPosition.X - player.X;
            long dy = altar.GridPosition.Y - player.Y;
            var distance2 = dx * dx + dy * dy;
            if (corpses > bestCorpses
                || corpses == bestCorpses && distance2 < bestDistance2)
            {
                best = altar;
                bestCorpses = corpses;
                bestDistance2 = distance2;
            }
        }
        return best;
    }
}

/// <summary>
/// Remembers Cloister monsters while they are streamed and freezes a per-altar corpse census
/// when the map sweep finishes. Freezing is essential: Ritual respawns use new entity IDs and
/// must not change the ordering of the remaining altars mid-chain.
/// </summary>
public sealed class RitualPriorityTracker
{
    private sealed class MonsterState
    {
        public Vector2i Position;
        public bool SeenAlive;
        public bool Dead;
    }

    private readonly Dictionary<uint, MonsterState> _monsters = new();
    private readonly Dictionary<uint, Vector2i> _altars = new();
    private Dictionary<uint, int>? _frozenScores;

    // Corpse identity + radius. Default to the live-validated Cloister values; the map-farming
    // strategy overrides them per run via Configure so the tracker is strategy-driven without
    // changing behavior for the Cloister preset (whose values equal these defaults).
    private string _monsterFragment = StackedDeckPolicy.CloisterMonsterPathFragment;
    private float _corpseRadiusGrid = StackedDeckPolicy.RitualCorpseRadiusGrid;

    /// <summary>Set the tracked corpse-monster metadata fragment and resurrection radius.</summary>
    public void Configure(string monsterFragment, float corpseRadiusGrid)
    {
        if (!string.IsNullOrWhiteSpace(monsterFragment)) _monsterFragment = monsterFragment;
        if (corpseRadiusGrid > 0) _corpseRadiusGrid = corpseRadiusGrid;
    }

    public bool IsFrozen => _frozenScores is not null;
    public int PrioritySeen => _monsters.Count;
    public int PriorityDead => _monsters.Values.Count(monster => monster.Dead);
    public int AltarsTracked => _altars.Count;
    public IReadOnlyDictionary<uint, int> Scores => _frozenScores ??
        _altars.ToDictionary(pair => pair.Key, pair => CountDeadNear(pair.Value));

    public void Observe(EntityCache cache, Vector2i player)
    {
        var present = new HashSet<uint>();
        foreach (var entry in cache.Entries.Values)
        {
            if (entry.Kind != EntityListReader.EntityKind.Monster
                || !entry.IsHostileMonster
                || !entry.Path.Contains(_monsterFragment, StringComparison.OrdinalIgnoreCase))
                continue;

            present.Add(entry.Id);
            if (!_monsters.TryGetValue(entry.Id, out var state))
                _monsters[entry.Id] = state = new MonsterState();
            state.Position = entry.GridPosition;
            if (entry.HpCurrent > 0) state.SeenAlive = true;
            if (entry.HpMax > 0 && entry.HpCurrent <= 0) state.Dead = true;
        }

        // EntityCache only evicts an absent entry after several healthy walks while its
        // last position is in the network bubble. Treat that disappearance as inferred
        // death; never infer from a degraded scan or an out-of-bubble position.
        if (!cache.LastScanHealth.Healthy) return;
        var bubble = GridConstants.NetworkBubbleGrid;
        var bubble2 = (long)bubble * bubble;
        foreach (var (id, state) in _monsters)
        {
            if (state.Dead || !state.SeenAlive || present.Contains(id)) continue;
            long dx = state.Position.X - player.X;
            long dy = state.Position.Y - player.Y;
            if (dx * dx + dy * dy <= bubble2) state.Dead = true;
        }
    }

    public void RegisterAltars(IEnumerable<MechanicEntry> mechanics)
    {
        foreach (var mechanic in mechanics)
            if (mechanic.Kind == MechanicKind.RitualRune)
                _altars[mechanic.Id] = mechanic.GridPosition;
    }

    public void Freeze()
    {
        if (_frozenScores is not null) return;
        _frozenScores = new Dictionary<uint, int>(_altars.Count);
        foreach (var (id, position) in _altars)
            _frozenScores[id] = CountDeadNear(position);
    }

    public int CorpseCount(uint altarId)
    {
        if (_frozenScores is not null)
            return _frozenScores.GetValueOrDefault(altarId);
        return _altars.TryGetValue(altarId, out var position) ? CountDeadNear(position) : 0;
    }

    public void Reset()
    {
        _monsters.Clear();
        _altars.Clear();
        _frozenScores = null;
    }

    private int CountDeadNear(Vector2i center)
    {
        var radius2 = _corpseRadiusGrid * _corpseRadiusGrid;
        var count = 0;
        foreach (var monster in _monsters.Values)
        {
            if (!monster.Dead) continue;
            var dx = (float)(monster.Position.X - center.X);
            var dy = (float)(monster.Position.Y - center.Y);
            if (dx * dx + dy * dy <= radius2) count++;
        }
        return count;
    }
}

/// <summary>
/// Text-only Eldritch altar scoring. Runtime UI discovery remains fail-closed until the
/// current-build altar label tree has been captured, but keeping scoring pure makes the
/// eventual click controller deterministic and replay-testable.
/// </summary>
public static class EldritchAltarScoring
{
    public static readonly IReadOnlyDictionary<string, int> StackedDeckWeights = BuildWeights();

    /// <summary>Any single line at or below this weight disqualifies its choice outright.</summary>
    public const int VetoWeight = -300;

    /// <summary>
    /// Build-killer mod classes, matched by normalized SUBSTRING. The exact-key weight table
    /// can't veto wording variants ("of Life and Energy Shield" vs "of Life, Mana and Energy
    /// Shield"): an unmatched variant scores 0, reads harmless, and gets taken — which killed
    /// a Righteous Fire character live on 2026-07-15. Anything containing these fragments is
    /// vetoed regardless of exact phrasing.
    /// </summary>
    private static readonly string[] VetoFragments =
    {
        Normalize("reduced Recovery Rate"),                          // regen/RF death spiral
        Normalize("Chaos Damage per second during any Flask Effect"),
    };

    public enum AltarDecision { Skip, Top, Bottom }

    /// <summary>Per-choice breakdown; surfaced through telemetry so weight tuning has data.</summary>
    public sealed record ChoiceAnalysis(
        int Reward, int Total, bool Vetoed, IReadOnlyList<string> UnknownLines);

    public sealed record AltarVerdict(
        AltarDecision Decision, string Reason, ChoiceAnalysis Top, ChoiceAnalysis Bottom);

    /// <summary>
    /// Pick which altar option to click. Policies 1/2 are literal (always top/bottom).
    /// Policy 3 ranks by <b>reward</b> — the sum of positive-weight lines only — because
    /// downside mods are transient map debuffs a farming build shrugs off; they matter
    /// as tie-breakers (total score) and as hard vetoes (any line ≤ <see cref="VetoWeight"/>).
    /// Anything else (including policy 0) returns Skip.
    /// </summary>
    public static AltarVerdict Decide(
        int policy, string topText, string bottomText,
        IReadOnlyDictionary<string, int>? overrides = null)
    {
        var top = Analyze(topText, overrides);
        var bottom = Analyze(bottomText, overrides);
        var (decision, reason) = policy switch
        {
            1 => (AltarDecision.Top, "policy: always top"),
            2 => (AltarDecision.Bottom, "policy: always bottom"),
            3 => DecideSmart(top, bottom),
            _ => (AltarDecision.Skip, "policy: skip"),
        };
        return new AltarVerdict(decision, reason, top, bottom);
    }

    private static (AltarDecision, string) DecideSmart(ChoiceAnalysis top, ChoiceAnalysis bottom)
    {
        if (top.Vetoed && bottom.Vetoed) return (AltarDecision.Skip, "both choices carry a vetoed mod");
        if (top.Vetoed) return (AltarDecision.Bottom, "top vetoed");
        if (bottom.Vetoed) return (AltarDecision.Top, "bottom vetoed");
        if (top.Reward != bottom.Reward)
            return top.Reward > bottom.Reward
                ? (AltarDecision.Top, $"reward {top.Reward} > {bottom.Reward}")
                : (AltarDecision.Bottom, $"reward {bottom.Reward} > {top.Reward}");
        return top.Total >= bottom.Total
            ? (AltarDecision.Top, $"reward tied {top.Reward}; total {top.Total} >= {bottom.Total}")
            : (AltarDecision.Bottom, $"reward tied {top.Reward}; total {bottom.Total} > {top.Total}");
    }

    public static ChoiceAnalysis Analyze(string text, IReadOnlyDictionary<string, int>? overrides = null)
    {
        int reward = 0, total = 0;
        var vetoed = false;
        List<string>? unknown = null;
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.EndsWith(':')) continue; // "Player gains:" headers
            var key = Normalize(line);
            if (key.Length == 0) continue;
            if (overrides is not null && overrides.TryGetValue(key, out var weight)
                || StackedDeckWeights.TryGetValue(key, out weight))
            {
                total += weight;
                if (weight > 0) reward += weight;
                if (weight <= VetoWeight) vetoed = true;
            }
            else
            {
                (unknown ??= new List<string>()).Add(line);
            }

            // Substring safety net: kill-class fragments veto regardless of whether the
            // exact line matched a table entry (wording/number variants must not slip by).
            foreach (var fragment in VetoFragments)
            {
                if (!key.Contains(fragment, StringComparison.OrdinalIgnoreCase)) continue;
                vetoed = true;
                break;
            }
        }
        return new ChoiceAnalysis(reward, total, vetoed, unknown ?? (IReadOnlyList<string>)[]);
    }

    public static int ScoreChoice(string text, IReadOnlyDictionary<string, int>? overrides = null)
    {
        var total = 0;
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var key = Normalize(rawLine);
            if (key.Length == 0) continue;
            if (overrides is not null && overrides.TryGetValue(key, out var custom))
                total += custom;
            else if (StackedDeckWeights.TryGetValue(key, out var builtIn))
                total += builtIn;
        }
        return total;
    }

    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var buffer = new char[text.Length];
        var length = 0;
        var inTag = false;
        foreach (var character in text)
        {
            if (character == '<') { inTag = true; continue; }
            if (character == '>') { inTag = false; continue; }
            if (!inTag && char.IsLetter(character)) buffer[length++] = character;
        }
        return new string(buffer, 0, length);
    }

    private static IReadOnlyDictionary<string, int> BuildWeights()
    {
        var weights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Add(string display, int score) => weights[Normalize(display)] = score;

        // Stacked-deck multipliers and premium rewards.
        Add("Divination Cards dropped by slain Enemies have #% chance to be Duplicated", 100);
        Add("Basic Currency Items dropped by slain Enemies have #% chance to be Duplicated", 95);
        Add("#% increased Quantity of Items found in this Area", 90);
        Add("#% chance to drop an additional Divination Card which rewards League Currency", 85);
        Add("Final Boss drops # additional Divination Cards which reward League Currency", 85);
        Add("#% chance to drop an additional Divination Card which rewards Currency", 80);
        Add("Final Boss drops # additional Divination Cards which reward Currency", 80);
        Add("#% chance to drop an additional Divine Orb", 80);
        Add("Final Boss drops # additional Divine Orbs", 80);
        Add("#% increased Rarity of Items found in this Area", 60);
        Add("Maps dropped by slain Enemies have #% chance to be Duplicated", 50);
        Add("Scarabs dropped by slain Enemies have #% chance to be Duplicated", 20);
        Add("#% chance to drop an additional Divination Scarab", 15);

        // Flat "additional <currency>" rewards from the altar pool. Values are relative
        // preferences, not chaos prices. Unrecognized reward lines score 0 and surface in
        // ChoiceAnalysis.UnknownLines telemetry — grow this list from live captures.
        Add("#% chance to drop an additional Exalted Orb", 55);
        Add("Final Boss drops # additional Exalted Orbs", 55);
        Add("#% chance to drop an additional Chaos Orb", 40);
        Add("Final Boss drops # additional Chaos Orbs", 40);
        Add("#% chance to drop an additional Orb of Annulment", 25);
        Add("Final Boss drops # additional Orbs of Annulment", 25);
        Add("#% chance to drop an additional Vaal Orb", 15);
        Add("Final Boss drops # additional Vaal Orbs", 15);
        Add("#% chance to drop an additional Orb of Alchemy", 10);
        Add("Final Boss drops # additional Orbs of Alchemy", 10);
        Add("#% chance to drop an additional Orb of Alteration", 5);
        Add("Final Boss drops # additional Orbs of Alteration", 5);

        // Build-independent severe risks. A future build profile may override these.
        Add("#% reduced Recovery Rate of Life, Mana and Energy Shield per Endurance Charge", -500);
        Add("Take # Chaos Damage per second during any Flask Effect", -500);
        Add("Projectiles are fired in random directions", -100);
        Add("#% reduced Defences per Frenzy Charge", -100);
        Add("Non-Damaging Ailments you inflict are reflected back to you", -100);
        Add("Curses you inflict are reflected back to you", -100);
        Add("-#% to Chaos Resistance", -75);
        Add("Nearby Enemies Gain #% of their Physical Damage as Extra Chaos Damage", -75);
        Add("Gain # Grasping Vines per second while Stationary", -70);
        Add("Gain #% of Physical Damage as Extra Chaos Damage", -60);
        Add("-#% to Fire Resistance", -60);
        Add("-#% to Cold Resistance", -60);
        Add("-#% to Lightning Resistance", -60);
        Add("-#% additional Physical Damage Reduction", -55);
        Add("Gain #% of Physical Damage as Extra Cold Damage", -50);
        Add("Gain #% of Physical Damage as Extra Fire Damage", -50);
        Add("Gain #% of Physical Damage as Extra Lightning Damage", -50);
        Add("Damage Penetrates #% of Enemy Elemental Resistances", -45);
        Add("#% chance to be targeted by a Meteor when you use a Flask", -40);
        Add("Hits have #% chance to ignore Enemy Physical Damage Reduction", -40);
        Add("Skills fire # additional Projectiles", -25);
        Add("#% increased Area of Effect", -20);
        Add("#% increased Attack Speed", -20);
        Add("#% increased Cast Speed", -20);
        Add("#% increased Flask Charges used", -20);
        Add("#% reduced Flask Effect Duration", -20);
        Add("All Damage can Ignite", -20);
        Add("All Damage can Shock", -20);
        Add("#% increased Movement Speed", -15);
        Add("Hits always Ignite", -15);
        Add("Hits always Shock", -15);
        return weights;
    }
}
