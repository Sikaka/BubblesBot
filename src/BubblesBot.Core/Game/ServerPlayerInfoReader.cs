namespace BubblesBot.Core.Game;

/// <summary>
/// Reads the server-side "player information" sub-struct
/// (<c>IngameData → ServerData → *(+PlayerRelatedData) = ServerPlayerData</c>): passive/skill-point
/// counts, class/level, and the allocated-passive id array. This is the <b>read</b> side of the
/// campaign "assign skill points" capability — the authoritative point budget a passive allocator
/// (A-10) must consult before spending a point, and the allocated-node set it must diff after.
///
/// <para><b>Trusted fields (validated 2026-07-19 vs operator ground truth 7 free / 5 refund):</b>
/// <see cref="Info.PlayerClass"/>, <see cref="Info.Level"/>, <see cref="Info.PassiveRefundPointsLeft"/>
/// (0x278), and <see cref="Info.FreePassiveSkillPointsLeft"/> (0x2D0 — ExileCore's 0x280 is stale this
/// patch). <b>Do not trust</b> <see cref="Info.QuestPassiveSkillPoints"/> or the ascendancy counts yet:
/// their offsets predate the struct's growth and read 0 on the validation character; they need a
/// character that has those points non-zero before promotion.</para>
///
/// <para>Reads are defensive: a torn-down or reallocated pointer yields <see cref="Unavailable"/>,
/// never a throw. The struct pointer is fetched fresh each call — never cache the address across
/// area transitions.</para>
/// </summary>
public static class ServerPlayerInfoReader
{
    public sealed record Info(
        bool IsAvailable,
        int PlayerClass,
        int Level,
        int PassiveRefundPointsLeft,
        int QuestPassiveSkillPoints,
        int FreePassiveSkillPointsLeft,
        int TotalAscendancyPoints,
        int SpentAscendancyPoints,
        long AllocatedPassiveSpanBytes,
        nint StructAddress)
    {
        /// <summary>Total passive points still available to spend (level-earned free + quest points).
        /// See the reliability caveat on <see cref="ServerPlayerInfoReader"/> — reconcile with U-08.</summary>
        public int AvailablePassivePoints => FreePassiveSkillPointsLeft + QuestPassiveSkillPoints;

        /// <summary>Ascendancy points still available to allocate.</summary>
        public int AvailableAscendancyPoints => TotalAscendancyPoints - SpentAscendancyPoints;
    }

    public static readonly Info Unavailable =
        new(false, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    public static Info Read(MemoryReader reader, nint ingameDataAddress)
    {
        if (!reader.TryReadStruct<nint>(ingameDataAddress + KnownOffsets.IngameData.ServerData, out var serverData)
            || serverData == 0)
            return Unavailable;

        if (!reader.TryReadStruct<nint>(serverData + KnownOffsets.ServerData.PlayerRelatedData, out var pi)
            || pi == 0)
            return Unavailable;

        // Class is a single byte at 0x270; reading it as Int32 would fold in adjacent fields.
        if (!reader.TryReadStruct<byte>(pi + KnownOffsets.ServerPlayerData.PlayerClass, out var playerClass))
            return Unavailable;
        if (!reader.TryReadStruct<int>(pi + KnownOffsets.ServerPlayerData.CharacterLevel, out var level))
            return Unavailable;

        // A sane character is level 1..100 and a class byte 0..6. Fail closed on garbage — that
        // means the pointer chain is stale (mid-transition) rather than a real player.
        if (level is < 1 or > 100 || playerClass > 12)
            return Unavailable;

        reader.TryReadStruct<int>(pi + KnownOffsets.ServerPlayerData.PassiveRefundPointsLeft, out var refund);
        reader.TryReadStruct<int>(pi + KnownOffsets.ServerPlayerData.QuestPassiveSkillPoints, out var quest);
        reader.TryReadStruct<int>(pi + KnownOffsets.ServerPlayerData.FreePassiveSkillPointsLeft, out var free);
        reader.TryReadStruct<int>(pi + KnownOffsets.ServerPlayerData.TotalAscendencyPoints, out var ascTotal);
        reader.TryReadStruct<int>(pi + KnownOffsets.ServerPlayerData.SpentAscendencyPoints, out var ascSpent);

        // Allocated-passive id array: expose only the raw byte span for now. The element stride
        // (2- vs 4-byte id) is unconfirmed because the array was empty on the validation character;
        // decoding into ids is deferred to S/D on a character that has allocated passives.
        long allocatedSpan = 0;
        if (reader.TryReadStruct<StdVector>(pi + KnownOffsets.ServerPlayerData.PassiveSkillIds, out var alloc)
            && alloc.First != 0 && alloc.ByteCount >= 0 && alloc.ByteCount < 4096)
            allocatedSpan = alloc.ByteCount;

        return new Info(true, playerClass, level, refund, quest, free, ascTotal, ascSpent, allocatedSpan, pi);
    }
}
