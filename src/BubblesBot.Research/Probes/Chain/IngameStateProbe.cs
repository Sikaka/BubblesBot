using BubblesBot.Core.Game;
using BubblesBot.Research.Probing;

namespace BubblesBot.Research.Probes.Chain;

/// <summary>IngameState's four root pointers: Data, Camera, IngameUi, UIRoot.</summary>
public sealed class IngameStateProbe : IProbe
{
    public string Name => "ingamestate.pointers";
    public string Group => "chain";
    public string Description => "IngameState.Data/Camera/IngameUi/UIRoot resolve.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx)
    {
        var igs = ctx.Chain.IngameState;
        return ProbeResult.Combine(
            Field(ctx, igs, KnownOffsets.IngameState.Data,    "ingameData",  "IngameState.Data",    Reads.Readable),
            Field(ctx, igs, KnownOffsets.IngameState.Camera,  "camera",      "IngameState.Camera",  Reads.Readable),
            Field(ctx, igs, KnownOffsets.IngameState.IngameUi,"ingameUi",    "IngameState.IngameUi",Reads.Readable),
            Field(ctx, igs, KnownOffsets.IngameState.UIRoot,  "uiRoot",      "IngameState.UIRoot",  Reads.IsElement));
    }

    public ProbeResult Discover(ProbeContext ctx)
        => Discovery.Pointer(ctx, ctx.Chain.IngameState, "ingameData", 0x800, "IngameState.Data");

    private static ProbeResult Field(ProbeContext ctx, nint baseAddr, int off, string oracleKey, string where,
        Func<BubblesBot.Core.MemoryReader, nint, bool> sound)
    {
        var ptr = ctx.Reader.TryReadStruct<nint>(baseAddr + off, out var p) ? p : 0;
        return Check.Address(ctx, oracleKey, ptr, $"{where}@+0x{off:X}", requireNonNull: true, a => sound(ctx.Reader, a));
    }
}
