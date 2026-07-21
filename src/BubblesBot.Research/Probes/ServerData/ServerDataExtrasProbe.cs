using BubblesBot.Core.Game;
using BubblesBot.Research.Probing;

namespace BubblesBot.Research.Probes.ServerData;

/// <summary>
/// Volatile/structural ServerData fields beyond league + monster level: latency (live), and the
/// 13-entry skill-bar gem-id array (structural sanity). Migrated from ServerDataExtrasTests.
/// </summary>
public sealed class ServerDataExtrasProbe : IProbe
{
    public string Name => "serverdata.extras";
    public string Group => "serverdata";
    public string Description => "ServerData latency live + SkillBarIds array sane.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx)
    {
        var sd = ctx.Chain.ServerData;
        if (sd == 0) return ProbeResult.Fail("ServerData pointer null");

        // Latency jitters by a few ms between our read and any oracle eval, so range-check it
        // (with an oracle, confirm we're within a tolerance of its value rather than exact).
        ProbeResult latency;
        if (!ctx.Reader.TryReadStruct<int>(sd + KnownOffsets.ServerData.Latency, out var lat))
            latency = ProbeResult.Fail("Latency unreadable");
        else if (lat is < 0 or > 60_000)
            latency = ProbeResult.Fail($"ServerData.Latency implausible ({lat})");
        else if (ctx.Oracle.IsAvailable && ctx.Oracle.TryGetValue("serverdata.latency", out var os) && int.TryParse(os, out var ol))
            latency = Math.Abs(lat - ol) <= 25
                ? ProbeResult.Pass($"ServerData.Latency = {lat}ms (oracle {ol}ms, within tolerance)")
                : ProbeResult.Fail($"ServerData.Latency = {lat}ms but oracle = {ol}ms (beyond jitter tolerance)");
        else
            latency = ProbeResult.Pass($"ServerData.Latency = {lat}ms (plausible; no oracle)");

        // SkillBarIds: 13 consecutive UInt16 gem ids. At least one non-zero on a real character,
        // and all within a sane id range.
        var ids = new List<ushort>();
        var ok = true;
        for (var i = 0; i < 13 && ok; i++)
            if (ctx.Reader.TryReadStruct<ushort>(sd + KnownOffsets.ServerData.SkillBarIds + i * 2, out var id)) ids.Add(id);
            else ok = false;
        var skillbar = !ok
            ? ProbeResult.Fail("SkillBarIds unreadable")
            : ids.Any(v => v != 0) && ids.All(v => v < 60_000)
                ? ProbeResult.Pass($"SkillBarIds [{string.Join(",", ids)}]")
                : ProbeResult.Fail($"SkillBarIds implausible [{string.Join(",", ids)}]");

        return ProbeResult.Combine(latency, skillbar);
    }

    public ProbeResult Discover(ProbeContext ctx)
    {
        var sd = ctx.Chain.ServerData;
        if (sd == 0) return ProbeResult.Found("ServerData.Latency", []);
        return Discovery.IntValue(ctx, sd, "serverdata.latency", 0xE000, "ServerData.Latency");
    }
}
