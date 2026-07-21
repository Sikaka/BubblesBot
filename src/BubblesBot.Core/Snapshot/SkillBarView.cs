using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Shape of one skill-bar slot. <see cref="GemId"/> is PoE's internal gem identifier (a
/// UInt16 from the SkillBarIds array). Mapping gem-id → human gem name requires a data file
/// lookup that v1 doesn't ship — combat behaviors should reference slots by index, not by
/// gem name. Cooldown is a placeholder until <c>ActorSkillCooldown</c> reads are validated.
/// </summary>
public readonly record struct SkillBarSlot(int Index, ushort GemId, bool OnCooldown)
{
    public bool   IsEmpty => GemId == 0;
}

/// <summary>
/// 13-slot skill bar view. Mirrors PoE's <c>SkillBarIds</c> array at
/// <c>ServerData + 0xC1D8</c> — slot 0 is the first hotbar position.
///
/// <para><b>Cooldown reads are stubbed.</b> The cooldown array layout (
/// <c>ActorComponent.ActorSkillsCooldownArray</c>) is unverified. Until the Research project
/// validates it, every slot reports <c>OnCooldown == false</c>. Combat behaviors should layer
/// a <c>Cooldown</c> composer on top of <c>Cast</c> as a fallback throttle.</para>
/// </summary>
public sealed class SkillBarView
{
    public const int SlotCount = 13;

    private readonly SkillBarSlot[] _slots = new SkillBarSlot[SlotCount];
    public IReadOnlyList<SkillBarSlot> Slots => _slots;

    internal SkillBarView(MemoryReader reader, nint serverDataAddress)
    {
        Span<byte> bytes = stackalloc byte[SlotCount * sizeof(ushort)];
        if (reader.TryReadBytes(serverDataAddress + KnownOffsets.ServerData.SkillBarIds, bytes) != bytes.Length)
            return;
        for (var i = 0; i < SlotCount; i++)
        {
            var id = BitConverter.ToUInt16(bytes.Slice(i * 2, 2));
            // OnCooldown stub — wire up via ActorSkillCooldown reads once validated.
            _slots[i] = new SkillBarSlot(i, id, OnCooldown: false);
        }
    }

    public SkillBarSlot this[int index] => _slots[index];
}
