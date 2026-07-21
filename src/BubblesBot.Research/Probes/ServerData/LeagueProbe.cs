using BubblesBot.Core.Game;
using BubblesBot.Research.Probing;
using BubblesBot.Research.Probing.Toolkit;

namespace BubblesBot.Research.Probes.ServerData;

/// <summary>
/// ServerData's League string (e.g. "Settlers"). Demonstrates string validation + string-search
/// discovery: when the offset drifts, scan memory for the known league text and report hits that
/// fall inside the ServerData struct as candidate offsets.
/// </summary>
public sealed class LeagueProbe : IProbe
{
    public string Name => "serverdata.league";
    public string Group => "serverdata";
    public string Description => "ServerData.League matches baseline league name.";
    public IReadOnlyList<string> RequiredFacts => ["league"];

    public ProbeResult Validate(ProbeContext ctx)
    {
        var sd = ctx.Chain.ServerData;
        if (sd == 0) return ProbeResult.Fail("ServerData pointer null");
        var league = ReadLeague(ctx, sd);
        if (league.Length == 0) return ProbeResult.Fail($"empty/garbage League at ServerData+0x{KnownOffsets.ServerData.League:X}");
        return Check.Str(ctx, "league", league, "ServerData.League");
    }

    public ProbeResult Discover(ProbeContext ctx)
    {
        if (!TryTarget(ctx, "league", out var league))
            return ProbeResult.Found("ServerData.League", []);

        var sd = ctx.Chain.ServerData;
        // The league string is stored inline in ServerData; find the UTF-16 bytes and report any
        // hit that lands inside the struct as a candidate field offset.
        const long span = 0x20000;
        var cands = MemScan.RegionsUtf16(ctx.Reader, league, max: 64)
            .Where(a => sd != 0 && (long)a >= (long)sd && (long)a < (long)sd + span)
            .Select(a => new OffsetCandidate((int)((long)a - (long)sd), $"\"{league}\" inline in ServerData"));
        return ProbeResult.Found("ServerData.League", cands);
    }

    private static string ReadLeague(ProbeContext ctx, nint sd)
    {
        if (!ctx.Reader.TryReadStruct<NativeUtf16Text>(sd + KnownOffsets.ServerData.League, out var t)) return "";
        var len = (int)t.Length;
        if (len is <= 0 or > 64) return "";
        return len <= 7
            ? ctx.Reader.ReadStringUtf16(sd + KnownOffsets.ServerData.League, len)
            : ctx.Reader.ReadStringUtf16(t.Buffer, len);
    }

    private static bool TryTarget(ProbeContext ctx, string key, out string value)
    {
        if (ctx.Oracle.IsAvailable && ctx.Oracle.TryGetValue(key, out value) && value.Length > 0) return true;
        return ctx.Facts.TryGetStr(key, out value);
    }
}
