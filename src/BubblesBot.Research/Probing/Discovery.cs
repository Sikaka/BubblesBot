using BubblesBot.Research.Probing.Toolkit;

namespace BubblesBot.Research.Probing;

/// <summary>
/// Shared Discover-path helpers so probes stay tiny. A pointer offset is located by scanning the
/// parent window for the oracle-supplied address (only possible with an oracle); a scalar offset
/// by scanning for the target value (oracle, else baseline). Without any target, returns no
/// candidates — fall back to <c>--dump &lt;base&gt;</c> and eyeball.
/// </summary>
public static class Discovery
{
    public static ProbeResult Pointer(ProbeContext ctx, nint parentBase, string oracleKey, int window, string field)
    {
        if (!ctx.Oracle.IsAvailable || !ctx.Oracle.TryGetAddress(oracleKey, out var target))
            return ProbeResult.Found(field, []);
        var cands = MemScan.WindowPtr(ctx.Reader, parentBase, window, target)
            .Select(o => new OffsetCandidate(o, $"-> 0x{(long)target:X}"));
        return ProbeResult.Found(field, cands);
    }

    public static ProbeResult IntValue(ProbeContext ctx, nint parentBase, string key, int window, string field)
    {
        if (!TryTargetInt(ctx, key, out var v))
            return ProbeResult.Found(field, []);
        var cands = MemScan.WindowInt32(ctx.Reader, parentBase, window, v)
            .Select(o => new OffsetCandidate(o, $"(={v})"));
        return ProbeResult.Found(field, cands);
    }

    private static bool TryTargetInt(ProbeContext ctx, string key, out int value)
    {
        if (ctx.Oracle.IsAvailable && ctx.Oracle.TryGetValue(key, out var os) && int.TryParse(os, out value)) return true;
        return ctx.Facts.TryGetInt(key, out value);
    }
}
