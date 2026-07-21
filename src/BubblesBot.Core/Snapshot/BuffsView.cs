using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>One active buff/debuff on an entity.</summary>
public readonly record struct BuffView(string Name, int Charges, float TimeRemaining, float MaxTime)
{
    public bool IsExpired => TimeRemaining <= 0f;
}

/// <summary>
/// All buffs currently on the player (or any entity). Buff name is read from the buff DAT
/// pointer's first string field; if that read fails the buff is reported with name "?".
///
/// <para><b>Offset caveat.</b> <c>BuffsComponent.Buffs</c> at <c>+0x160</c> is unverified. If
/// the count looks insane (&gt;128) we bail rather than walk garbage memory.</para>
/// </summary>
public sealed class BuffsView
{
    private readonly IReadOnlyList<BuffView> _buffs;

    public IReadOnlyList<BuffView> Buffs => _buffs;
    public bool Has(string name)
    {
        foreach (var b in _buffs) if (string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public BuffView? Find(string name)
    {
        foreach (var b in _buffs) if (string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)) return b;
        return null;
    }

    internal BuffsView(MemoryReader reader, nint buffsComponentAddress)
    {
        if (buffsComponentAddress == 0) { _buffs = Array.Empty<BuffView>(); return; }
        // NativePtrArray = (begin, end, capacity-end) of pointers.
        if (!reader.TryReadStruct<nint>(buffsComponentAddress + KnownOffsets.BuffsComponent.Buffs, out var begin) || begin == 0
         || !reader.TryReadStruct<nint>(buffsComponentAddress + KnownOffsets.BuffsComponent.Buffs + 8, out var end))
        { _buffs = Array.Empty<BuffView>(); return; }

        var byteLen = (long)end - (long)begin;
        if (byteLen <= 0 || byteLen > 128 * 8) { _buffs = Array.Empty<BuffView>(); return; }
        var count = (int)(byteLen / 8);

        var list = new List<BuffView>(count);
        Span<byte> ptrBuf = stackalloc byte[8];
        for (var i = 0; i < count; i++)
        {
            if (reader.TryReadBytes(begin + i * 8, ptrBuf) != 8) continue;
            var buffPtr = (nint)BitConverter.ToInt64(ptrBuf);
            if (buffPtr == 0) continue;
            list.Add(ReadOne(reader, buffPtr));
        }
        _buffs = list;
    }

    private static BuffView ReadOne(MemoryReader reader, nint buffAddr)
    {
        reader.TryReadStruct<nint>(buffAddr  + KnownOffsets.Buff.BuffDatPtr, out var datPtr);
        reader.TryReadStruct<float>(buffAddr + KnownOffsets.Buff.MaxTime,    out var maxTime);
        reader.TryReadStruct<float>(buffAddr + KnownOffsets.Buff.Timer,      out var timer);
        reader.TryReadStruct<ushort>(buffAddr + KnownOffsets.Buff.Charges,   out var charges);

        var name = "?";
        if (datPtr != 0)
        {
            // BuffDefinition row layout: pointer at +0x0 → null-terminated UTF-16 internal id
            // ("tukohama_god_stationary_buff" etc). Validated 2026-05-06 against POEMCP's
            // BuffDefinition.Id by dumping bytes at the deref'd address.
            if (reader.TryReadStruct<nint>(datPtr, out var namePtr) && namePtr != 0)
                try { name = reader.ReadStringUtf16(namePtr, maxChars: 96); }
                catch { name = "?"; }
        }

        return new BuffView(name, charges, timer, maxTime);
    }
}
