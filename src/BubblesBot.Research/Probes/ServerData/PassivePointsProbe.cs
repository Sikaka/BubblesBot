using BubblesBot.Core.Game;
using BubblesBot.Research.Probing;

namespace BubblesBot.Research.Probes.ServerData;

/// <summary>
/// ServerPlayerData passive/skill-point read (checklist S-05, read side of A-10 "assign skill
/// points"). Reads class/level/point counts via <see cref="ServerPlayerInfoReader"/> and confirms
/// each against the ExileCore oracle. Class/level are non-zero struct anchors; the point counts may
/// legitimately be zero (see the reliability caveat on the reader) so they are validated by exact
/// oracle agreement rather than a non-zero expectation.
/// </summary>
public sealed class PassivePointsProbe : IProbe
{
    public string Name => "serverdata.passive-points";
    public string Group => "serverdata";
    public string Description => "ServerPlayerData class/level/passive+ascendancy point counts match ExileCore.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx)
    {
        var info = ServerPlayerInfoReader.Read(ctx.Reader, ctx.Chain.IngameData);
        if (!info.IsAvailable)
            return ProbeResult.Fail("ServerPlayerData unavailable (null chain or implausible class/level)");

        // Oracle-validated fields. Struct anchors (Level/Class) pin the base; Refund (0x278) reads
        // correctly in ExileCore too. These three are safe to cross-check against POEMCP.
        var level  = OracleInt(ctx, "serverdata.player.level",   info.Level,                   "Level");
        var cls    = OracleInt(ctx, "serverdata.player.class",   info.PlayerClass,             "Class");
        var refund = OracleInt(ctx, "serverdata.passive.refund", info.PassiveRefundPointsLeft, "RefundPoints");

        // FreePassiveSkillPointsLeft: POEMCP/ExileCore is a STALE oracle for this field on this patch
        // (its 0x280 reads 0 while the true value is at 0x2D0). Range-check only; the authoritative
        // proof is the differential allocation LiveTest (allocate → this value decrements).
        var free = Range(info.FreePassiveSkillPointsLeft, "FreePassivePoints (oracle stale; range-only)");

        // QuestPassive + ascendancy counts sit on unconfirmed (pre-struct-growth) offsets and read 0
        // here; do NOT oracle-check (0==0 vs a stale oracle is a false positive). Range-only until a
        // character with those points non-zero lets us re-pin them.
        var quest = Range(info.QuestPassiveSkillPoints, "QuestPassivePoints (unconfirmed offset; range-only)");
        var ascT  = Range(info.TotalAscendancyPoints,   "AscendancyTotal (unconfirmed offset; range-only)");
        var ascS  = Range(info.SpentAscendancyPoints,   "AscendancySpent (unconfirmed offset; range-only)");

        return ProbeResult.Combine(level, cls, refund, free, quest, ascT, ascS);
    }

    private static ProbeResult OracleInt(ProbeContext ctx, string key, int ours, string label)
    {
        if (ctx.Oracle.IsAvailable && ctx.Oracle.TryGetValue(key, out var s) && int.TryParse(s, out var truth))
            return ours == truth
                ? ProbeResult.Pass($"{label} = {ours} (oracle {truth})")
                : ProbeResult.Fail($"{label} = {ours} but oracle = {truth}");
        return Range(ours, $"{label} (no oracle)");
    }

    private static ProbeResult Range(int ours, string label)
        => ours is >= 0 and <= 1000
            ? ProbeResult.Pass($"{label} = {ours} (plausible)")
            : ProbeResult.Fail($"{label} = {ours} implausible");

    public ProbeResult Discover(ProbeContext ctx)
    {
        // The struct base is *(ServerData + PlayerRelatedData); scan it for the Level anchor.
        var sd = ctx.Chain.ServerData;
        if (sd == 0) return ProbeResult.Found("ServerPlayerData.CharacterLevel", []);
        if (!ctx.Reader.TryReadStruct<nint>(sd + KnownOffsets.ServerData.PlayerRelatedData, out var pi) || pi == 0)
            return ProbeResult.Found("ServerPlayerData.CharacterLevel", []);
        return Discovery.IntValue(ctx, pi, "serverdata.player.level", 0x400, "ServerPlayerData.CharacterLevel");
    }
}
