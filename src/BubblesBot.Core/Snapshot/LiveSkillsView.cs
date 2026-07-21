using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// One slot's live readout. The id comes from the skill bar and is used for cooldown reads;
/// the user-facing name is resolved from the matching live ActorSkill record.
/// </summary>
public readonly record struct LiveSkillEntry(
    int BarSlot,
    ushort GemId,
    string Name,
    bool IsReady,
    int MaxUses);

/// <summary>
/// Reads the player's currently-bound skills with live names and cooldown state. ActorSkill
/// ids are session-local, so name resolution must happen against the current player rather
/// than through a hardcoded id catalog.
/// </summary>
public sealed class LiveSkillsView
{
    private readonly IReadOnlyList<LiveSkillEntry> _entries;
    public IReadOnlyList<LiveSkillEntry> Entries => _entries;

    internal LiveSkillsView(GameSnapshot snapshot, nint serverDataAddress, nint actorComponentAddress)
    {
        var reader = snapshot.Reader;
        var list = new List<LiveSkillEntry>();
        if (serverDataAddress == 0)
        {
            _entries = list;
            return;
        }

        Span<byte> bytes = stackalloc byte[SkillBarView.SlotCount * sizeof(ushort)];
        if (reader.TryReadBytes(serverDataAddress + KnownOffsets.ServerData.SkillBarIds, bytes) != bytes.Length)
        {
            _entries = list;
            return;
        }

        var ids = new ushort[SkillBarView.SlotCount];
        for (var index = 0; index < ids.Length; index++)
            ids[index] = BitConverter.ToUInt16(bytes.Slice(index * 2, 2));

        var names = ActorSkillNameReader.Read(snapshot, actorComponentAddress,
            ids.Where(id => id != 0).Distinct().ToArray());
        var cooldowns = new SkillCooldownReader(reader);
        for (var index = 0; index < ids.Length; index++)
        {
            var id = ids[index];
            if (id == 0) continue;
            var cooldown = actorComponentAddress != 0 ? cooldowns.Read(actorComponentAddress, id) : null;
            list.Add(new LiveSkillEntry(
                BarSlot: index,
                GemId: id,
                Name: names.TryGetValue(id, out var resolved) ? resolved : string.Empty,
                IsReady: cooldown is null || cooldown.Value.IsReady,
                MaxUses: cooldown?.MaxUses ?? 0));
        }

        _entries = list;
    }
}
