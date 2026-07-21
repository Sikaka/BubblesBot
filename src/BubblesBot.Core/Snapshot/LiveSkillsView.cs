using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// One slot's live readout: gem id from the skill bar, current cooldown state, hotbar
/// position. <see cref="Name"/> resolves to a friendly label when the gem id is in the
/// hardcoded catalog (<see cref="GemNames"/>); otherwise empty — the dashboard falls back
/// to showing the raw id for the user to copy. Live name reading from <c>ActorSkill</c>
/// memory is a follow-up once the chain through SubData is validated.
/// </summary>
public readonly record struct LiveSkillEntry(
    int    BarSlot,         // 0..12 in the SkillBarIds array
    ushort GemId,
    string Name,
    bool   IsReady,
    int    MaxUses);

/// <summary>
/// Reads the player's currently-equipped skills with live cooldown state. Built off the
/// validated <c>SkillBarIds</c> + <c>ActorSkillsCooldowns</c> readers. Surfaces in the
/// dashboard so the user can configure skill bindings without typing gem ids by hand.
/// </summary>
public sealed class LiveSkillsView
{
    private readonly IReadOnlyList<LiveSkillEntry> _entries;
    public IReadOnlyList<LiveSkillEntry> Entries => _entries;

    internal LiveSkillsView(MemoryReader reader, nint serverDataAddress, nint actorComponentAddress)
    {
        var list = new List<LiveSkillEntry>();
        if (serverDataAddress == 0) { _entries = list; return; }

        // Read all 13 hotbar gem ids in one batch.
        Span<byte> bytes = stackalloc byte[SkillBarView.SlotCount * sizeof(ushort)];
        if (reader.TryReadBytes(serverDataAddress + KnownOffsets.ServerData.SkillBarIds, bytes) != bytes.Length)
        {
            _entries = list;
            return;
        }

        // Build lookup of cooldown entries → gem id once, so we don't re-walk the array per slot.
        var cdReader = new SkillCooldownReader(reader);
        for (var i = 0; i < SkillBarView.SlotCount; i++)
        {
            var id = BitConverter.ToUInt16(bytes.Slice(i * 2, 2));
            if (id == 0) continue;
            var cd = actorComponentAddress != 0 ? cdReader.Read(actorComponentAddress, id) : null;
            var name = GemNames.TryGetValue(id, out var n) ? n : "";
            list.Add(new LiveSkillEntry(
                BarSlot: i,
                GemId: id,
                Name: name,
                IsReady: cd is null ? true : cd.Value.IsReady,
                MaxUses: cd?.MaxUses ?? 0));
        }
        _entries = list;
    }

    /// <summary>
    /// Hardcoded gem-id → friendly name map. Populated as we encounter skills via POEMCP.
    /// PoE rarely changes these ids across patches, so a manually-curated list works fine
    /// until we wire live name reads from ActorSkill memory.
    ///
    /// <para><b>This is a stopgap.</b> The proper fix is reading <c>ActorSkill.Name</c>
    /// directly via the SubData chain — the offset is known to ExileApi but unverified for
    /// us. When PoE patches do shift skill ids (rare), the dashboard will show "Skill #N"
    /// for unknown ids and the user can still import + label manually.</para>
    /// </summary>
    private static readonly IReadOnlyDictionary<ushort, string> GemNames = new Dictionary<ushort, string>
    {
        // Validated 2026-05-07 against the BawdyLotionMirage test character:
        [10505] = "Walk",                  // PoE's "move only" pseudo-skill on LMB / E
        [32770] = "Whirling Blades",
        [32777] = "Spirit Offering",
        [34825] = "Frostblink",
        [34824] = "Vaal Haste",
        [33801] = "Assassin's Mark",
        // Add more as discovered. Format: [gemId] = "Skill Name".
    };
}
