using BubblesBot.Core.Game;
using BubblesBot.Research.Probing;

namespace BubblesBot.Research.Probes.Chain;

/// <summary>IngameData's root pointers: LocalPlayer, EntityList, ServerData, CurrentArea.</summary>
public sealed class IngameDataProbe : IProbe
{
    public string Name => "ingamedata.pointers";
    public string Group => "chain";
    public string Description => "IngameData.LocalPlayer/EntityList/ServerData/CurrentArea resolve.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx)
    {
        var igd = ctx.Chain.IngameData;
        return ProbeResult.Combine(
            Field(ctx, igd, KnownOffsets.IngameData.LocalPlayer, "player",      "IngameData.LocalPlayer"),
            Field(ctx, igd, KnownOffsets.IngameData.EntityList,  "entityList",  "IngameData.EntityList"),
            Field(ctx, igd, KnownOffsets.IngameData.ServerData,  "serverData",  "IngameData.ServerData"),
            Field(ctx, igd, KnownOffsets.IngameData.CurrentArea, "currentArea", "IngameData.CurrentArea"));
    }

    public ProbeResult Discover(ProbeContext ctx)
        => Discovery.Pointer(ctx, ctx.Chain.IngameData, "player", 0x1000, "IngameData.LocalPlayer");

    private static ProbeResult Field(ProbeContext ctx, nint baseAddr, int off, string oracleKey, string where)
    {
        var ptr = ctx.Reader.TryReadStruct<nint>(baseAddr + off, out var p) ? p : 0;
        return Check.Address(ctx, oracleKey, ptr, $"{where}@+0x{off:X}", requireNonNull: true, a => Reads.Readable(ctx.Reader, a));
    }
}
