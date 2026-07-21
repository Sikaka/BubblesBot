using BubblesBot.Core.Game;
using BubblesBot.Research.Probing;
using BubblesBot.Research.Probing.Toolkit;

namespace BubblesBot.Research.Probes.Chain;

/// <summary>
/// IngameData's current-area level + hash. These values change with every prepared area, so normal
/// validation is structural; a baseline hash is used only by the explicit Discover path.
/// </summary>
public sealed class AreaProbe : IProbe
{
    public string Name => "area";
    public string Group => "chain";
    public string Description => "IngameData CurrentAreaLevel + CurrentAreaHash are live and structurally plausible.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx)
    {
        var igd = ctx.Chain.IngameData;

        var level = ctx.Reader.TryReadStruct<byte>(igd + KnownOffsets.IngameData.CurrentAreaLevel, out var lvl)
            ? lvl is >= 1 and <= 100
                ? ProbeResult.Pass($"IngameData.CurrentAreaLevel = {lvl}")
                : ProbeResult.Fail($"CurrentAreaLevel out of range: {lvl}")
            : ProbeResult.Fail($"unreadable CurrentAreaLevel at +0x{KnownOffsets.IngameData.CurrentAreaLevel:X}");

        var hash = ctx.Reader.TryReadStruct<uint>(igd + KnownOffsets.IngameData.CurrentAreaHash, out var h)
            ? h != 0
                ? ProbeResult.Pass($"IngameData.CurrentAreaHash = 0x{h:X8}")
                : ProbeResult.Fail("CurrentAreaHash is zero")
            : ProbeResult.Fail($"unreadable CurrentAreaHash at +0x{KnownOffsets.IngameData.CurrentAreaHash:X}");

        return ProbeResult.Combine(level, hash);
    }

    public ProbeResult Discover(ProbeContext ctx)
    {
        // Hash is the discriminating value; level (1..100) matches everywhere so we don't scan it.
        if (!TryTargetHash(ctx, out var hash))
            return ProbeResult.Found("IngameData.CurrentAreaHash", []);

        var needle = unchecked((int)hash);
        var cands = MemScan.WindowInt32(ctx.Reader, ctx.Chain.IngameData, window: 0x1200, needle)
            .Select(o => new OffsetCandidate(o, $"CurrentAreaHash (=0x{hash:X8})"));
        return ProbeResult.Found("IngameData.CurrentAreaHash", cands);
    }

    private static bool TryTargetHash(ProbeContext ctx, out long hash)
    {
        if (ctx.Oracle.IsAvailable && ctx.Oracle.TryGetValue("area.hash", out var os) && long.TryParse(os, out hash)) return true;
        return ctx.Facts.TryGetLong("area.hash", out hash);
    }
}
