using BubblesBot.Core.Game;
using BubblesBot.Research.Probing;

namespace BubblesBot.Research.Probes.ServerData;

/// <summary>ServerData.MonsterLevel — cross-checked against the independent IngameData area-level field.</summary>
public sealed class MonsterLevelProbe : IProbe
{
    public string Name => "serverdata.monsterlevel";
    public string Group => "serverdata";
    public string Description => "ServerData.MonsterLevel matches IngameData.CurrentAreaLevel.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx)
    {
        var sd = ctx.Chain.ServerData;
        if (sd == 0) return ProbeResult.Fail("ServerData pointer null");
        if (!ctx.Reader.TryReadStruct<byte>(sd + KnownOffsets.ServerData.MonsterLevel, out var ml))
            return ProbeResult.Fail($"unreadable MonsterLevel at +0x{KnownOffsets.ServerData.MonsterLevel:X}");
        if (!ctx.Reader.TryReadStruct<byte>(
                ctx.Chain.IngameData + KnownOffsets.IngameData.CurrentAreaLevel, out var areaLevel))
            return ProbeResult.Fail("IngameData.CurrentAreaLevel unreadable for cross-check");
        return ml == areaLevel && ml is >= 1 and <= 100
            ? ProbeResult.Pass($"ServerData.MonsterLevel = {ml}; IngameData agrees")
            : ProbeResult.Fail($"ServerData.MonsterLevel={ml} != IngameData.CurrentAreaLevel={areaLevel}");
    }

    public ProbeResult Discover(ProbeContext ctx)
    {
        var sd = ctx.Chain.ServerData;
        if (sd == 0) return ProbeResult.Found("ServerData.MonsterLevel", []);
        if (!ctx.Reader.TryReadStruct<byte>(
                ctx.Chain.IngameData + KnownOffsets.IngameData.CurrentAreaLevel, out var level))
            return ProbeResult.Found("ServerData.MonsterLevel", []);
        var bytes = new byte[0xE000];
        var read = ctx.Reader.TryReadBytes(sd, bytes);
        var candidates = new List<OffsetCandidate>();
        for (var offset = 0; offset < read; offset++)
            if (bytes[offset] == level)
                candidates.Add(new OffsetCandidate(offset, $"byte equals current area level {level}"));
        return ProbeResult.Found("ServerData.MonsterLevel", candidates);
    }
}
