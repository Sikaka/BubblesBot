using BubblesBot.Core.Game;
using BubblesBot.Research.Probing;

namespace BubblesBot.Research.Probes.Player;

/// <summary>
/// Buffs component: the buff array reads with a plausible count (live-matched to the oracle's buff
/// count when present) and the first buff struct is internally sane. Migrated from BuffsTests.
/// </summary>
public sealed class PlayerBuffsProbe : IProbe
{
    public string Name => "player.buffs";
    public string Group => "player";
    public string Description => "Buffs array count plausible; first buff struct sane.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx)
    {
        var buffs = ctx.Chain.PlayerComponent("Buffs");
        if (buffs == 0) return ProbeResult.Fail("no Buffs component");
        if (!ctx.Reader.TryReadStruct<NativePtrArray>(buffs + KnownOffsets.BuffsComponent.Buffs, out var arr))
            return ProbeResult.Fail($"buff array unreadable at +0x{KnownOffsets.BuffsComponent.Buffs:X}");
        if (arr.Count is < 0 or > 1000)
            return ProbeResult.Fail($"buff count implausible ({arr.Count})");

        var count = Check.Live(ctx, "buffs.count", arr.Count, "Buffs.Count", 0, 1000);
        if (arr.Count == 0) return ProbeResult.Combine(count, ProbeResult.Pass("no buffs to spot-check"));

        // First buff struct sanity (Timer in [0, MaxTime] unless permanent).
        ProbeResult buff = ProbeResult.Fail("first buff unreadable");
        if (ctx.Reader.TryReadStruct<nint>(arr.First, out var b0) && b0 != 0
            && ctx.Reader.TryReadStruct<Buff>(b0, out var bf))
        {
            var permanent = float.IsInfinity(bf.MaxTime) || float.IsNaN(bf.MaxTime);
            buff = permanent || (bf.MaxTime >= 0 && bf.Timer >= 0 && bf.Timer <= bf.MaxTime + 0.05f)
                ? ProbeResult.Pass($"buff0 {(permanent ? "permanent" : $"{bf.Timer:0.#}/{bf.MaxTime:0.#}s")} charges={bf.Charges}")
                : ProbeResult.Fail($"buff0 timer/maxtime inconsistent ({bf.Timer}/{bf.MaxTime})");
        }
        return ProbeResult.Combine(count, buff);
    }

    public ProbeResult Discover(ProbeContext ctx)
        // The array offset is exercised by Validate's count check; no scalar value-scan target.
        => ProbeResult.Found("BuffsComponent.Buffs", []);
}
