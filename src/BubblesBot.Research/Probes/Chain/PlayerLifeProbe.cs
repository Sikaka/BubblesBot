using BubblesBot.Core.Game;
using BubblesBot.Research.Probing;
using BubblesBot.Research.Probing.Toolkit;

namespace BubblesBot.Research.Probes.Chain;

/// <summary>
/// The player Life component's Health vital should read your current HP. Anchors the whole
/// component-map chain (entity -> details -> component lookup -> Life), so when this passes the
/// component resolution is sound; when it fails, the offset (or the chain above it) drifted.
/// </summary>
public sealed class PlayerLifeProbe : IProbe
{
    public string Name => "player.life";
    public string Group => "chain";
    public string Description => "Life component Health.Current matches baseline character.hp.";
    public IReadOnlyList<string> RequiredFacts => ["character.hp"];

    public ProbeResult Validate(ProbeContext ctx)
    {
        var life = ctx.Chain.PlayerComponent("Life");
        if (life == 0) return ProbeResult.Fail("no Life component on player (chain/component-map broke above this offset)");
        if (!ctx.Reader.TryReadStruct<VitalStruct>(life + KnownOffsets.LifeComponent.Health, out var hp))
            return ProbeResult.Fail($"could not read VitalStruct at Life+0x{KnownOffsets.LifeComponent.Health:X}");
        // HP is volatile: exact-match it only against a live oracle; otherwise assert it's a live,
        // internally-consistent value in [0, Max]. (Max is the stable check, in PlayerStatsProbe.)
        if (hp.Max is <= 0 or > 10_000_000)
            return ProbeResult.Fail($"Life.Health.Max implausible ({hp.Max}) -- offset likely drifted");
        return Check.Live(ctx, "character.hp", hp.Current, "Life.Health.Current", 0, hp.Max + 1);
    }

    public ProbeResult Discover(ProbeContext ctx)
    {
        var life = ctx.Chain.PlayerComponent("Life");
        if (life == 0) return ProbeResult.Found("LifeComponent.Health", []);

        if (!TryTarget(ctx, "character.hp", out var target))
            return ProbeResult.Found("LifeComponent.Health", []);

        var cands = MemScan.WindowInt32(ctx.Reader, life, window: 0x300, target)
            .Select(o => new OffsetCandidate(o - KnownOffsets.Vital.Current,
                $"Health base; Current(hp={target}) found at Life+0x{o:X}"));
        return ProbeResult.Found("LifeComponent.Health", cands);
    }

    private static bool TryTarget(ProbeContext ctx, string key, out int value)
    {
        if (ctx.Oracle.IsAvailable && ctx.Oracle.TryGetValue(key, out var os) && int.TryParse(os, out value)) return true;
        return ctx.Facts.TryGetInt(key, out value);
    }
}
