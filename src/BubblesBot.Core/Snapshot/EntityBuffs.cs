using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Lightweight per-entity buff checks for the entity cache's refresh path. Unlike
/// <see cref="BuffsView"/> (built once per snapshot for the player), this runs for every
/// nearby hostile at its refresh cadence — so it reads only buff-definition pointers and
/// memoizes datPtr → name for the session (buff-definition rows never move at runtime;
/// after warm-up a check is a handful of pointer reads plus dictionary hits).
///
/// <para>Layout mirrored from <see cref="BuffsView"/>; live-verified on MONSTER entities
/// 2026-07-14 via <c>--dump-entity-buffs</c> (dormant Vaal constructs read
/// "hidden_monster_disable_minions", player minions read their aura buffs).</para>
/// </summary>
public static class EntityBuffs
{
    private static readonly Dictionary<nint, string> _nameByDat = new();

    /// <summary>
    /// True when the entity carries a state buff that makes it an INVALID fight target
    /// right now:
    /// <list type="bullet">
    ///   <item><c>hidden_monster*</c> — dormant/unspawned (Vaal constructs, submerged
    ///     elementals; the <c>_disable_minions</c> variant also suppresses minion aggro).</item>
    ///   <item><c>frozen_in_time*</c> — essence-imprisoned monsters, boss phase intros.</item>
    /// </list>
    /// This is LIVE state, re-read per refresh — released/awakened mobs lose the buff and
    /// immediately become valid targets. Never cache or blacklist on top of this.
    /// </summary>
    public static bool TryHasInvalidTargetBuff(MemoryReader reader, nint buffsComponentAddress, out bool hasInvalidBuff)
    {
        hasInvalidBuff = false;
        if (buffsComponentAddress == 0) return true;
        if (!reader.TryReadStruct<nint>(buffsComponentAddress + KnownOffsets.BuffsComponent.Buffs, out var begin) || begin == 0
         || !reader.TryReadStruct<nint>(buffsComponentAddress + KnownOffsets.BuffsComponent.Buffs + 8, out var end))
            return false;

        var byteLen = (long)end - (long)begin;
        if (byteLen == 0) return true;
        if (byteLen < 0 || byteLen > 128 * 8) return false;
        var count = (int)(byteLen / 8);
        var complete = true;

        for (var i = 0; i < count; i++)
        {
            if (!reader.TryReadStruct<nint>(begin + i * 8, out var buffPtr) || buffPtr == 0)
            { complete = false; continue; }
            if (!reader.TryReadStruct<nint>(buffPtr + KnownOffsets.Buff.BuffDatPtr, out var datPtr) || datPtr == 0)
            { complete = false; continue; }

            if (!_nameByDat.TryGetValue(datPtr, out var name))
            {
                name = "?";
                if (reader.TryReadStruct<nint>(datPtr, out var namePtr) && namePtr != 0)
                    try { name = reader.ReadStringUtf16(namePtr, maxChars: 64); } catch { /* keep "?" */ }
                if (_nameByDat.Count > 4096) _nameByDat.Clear();   // area-churn safety valve
                _nameByDat[datPtr] = name;
            }

            if (name.StartsWith("hidden_monster", StringComparison.Ordinal)
             || name.StartsWith("frozen_in_time", StringComparison.Ordinal))
            {
                hasInvalidBuff = true;
                return true;
            }
        }
        return complete;
    }
}
