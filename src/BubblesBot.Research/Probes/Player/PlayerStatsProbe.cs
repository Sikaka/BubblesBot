using BubblesBot.Core.Game;
using BubblesBot.Research.Probing;

namespace BubblesBot.Research.Probes.Player;

/// <summary>Stable player scalars: character level, name, and max HP.</summary>
public sealed class PlayerStatsProbe : IProbe
{
    public string Name => "player.stats";
    public string Group => "player";
    public string Description => "Player level + name + Life.Health.Max match baseline.";
    public IReadOnlyList<string> RequiredFacts => ["character.level", "character.name", "character.maxhp"];

    public ProbeResult Validate(ProbeContext ctx)
    {
        var playerComp = ctx.Chain.PlayerComponent("Player");
        var life = ctx.Chain.PlayerComponent("Life");

        var level = playerComp != 0 && ctx.Reader.TryReadStruct<byte>(playerComp + KnownOffsets.PlayerComponent.Level, out var lvl)
            ? Check.Int(ctx, "character.level", lvl, "PlayerComponent.Level")
            : ProbeResult.Fail("PlayerComponent/Level unreadable");

        var name = playerComp != 0
            ? Check.Str(ctx, "character.name", ReadName(ctx, playerComp), "PlayerComponent.PlayerName")
            : ProbeResult.Fail("PlayerComponent unreadable");

        var maxHp = life != 0 && ctx.Reader.TryReadStruct<VitalStruct>(life + KnownOffsets.LifeComponent.Health, out var v)
            ? Check.Int(ctx, "character.maxhp", v.Max, "Life.Health.Max")
            : ProbeResult.Fail("Life.Health unreadable");

        return ProbeResult.Combine(level, name, maxHp);
    }

    public ProbeResult Discover(ProbeContext ctx)
    {
        var life = ctx.Chain.PlayerComponent("Life");
        if (life == 0) return ProbeResult.Found("Life.Health.Max", []);
        // Health.Max sits at Health + 0x2C; scan for the baseline/oracle max value.
        var r = Discovery.IntValue(ctx, life, "character.maxhp", 0x300, "Life.Health.Max(Current)");
        return r;
    }

    private static string ReadName(ProbeContext ctx, nint playerComp)
    {
        var at = playerComp + KnownOffsets.PlayerComponent.PlayerName;
        if (!ctx.Reader.TryReadStruct<NativeUtf16Text>(at, out var t)) return "";
        var len = (int)t.Length;
        if (len is <= 0 or > 64) return "";
        return len <= 7 ? ctx.Reader.ReadStringUtf16(at, len) : ctx.Reader.ReadStringUtf16(t.Buffer, len);
    }
}
