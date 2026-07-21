using BubblesBot.Core;
using BubblesBot.Core.Game;

namespace BubblesBot.Research.Validation.Tests;

/// <summary>
/// Read a specific field on a specific component and sanity-check the value.
/// If POEMCP is available, also assert exact equality with ExileCore's reading.
/// </summary>
public abstract class ComponentFieldTest : ValidationTest
{
    protected abstract string ComponentName { get; }
    protected abstract string PoemcpExpr { get; }
    public override string? Group => "Component fields";

    /// <summary>Read the field via our reader. Return (display, sanityOk).</summary>
    protected abstract (string Display, bool SanityOk, string? FailReason, object? Value) ReadOurs(MemoryReader reader, nint componentAddr);

    /// <summary>Compare the POEMCP truth value to ours. Override for type-specific compare.</summary>
    protected virtual bool TruthMatches(object? ours, EvalResult truth, out string compareDetail)
    {
        compareDetail = "";
        if (ours is null) return false;
        var truthStr = truth.AsString();
        var oursStr = ours.ToString();
        compareDetail = $"ours={oursStr} vs poemcp={truthStr}";
        return string.Equals(oursStr, truthStr, StringComparison.OrdinalIgnoreCase);
    }

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(ComponentLookupKeys.PlayerComponentMap, out var mObj)
            || mObj is not Dictionary<string, nint> map)
            return new TestOutcome.Skip(Name, "component map not resolved");

        if (!map.TryGetValue(ComponentName, out var compAddr))
            return new TestOutcome.Fail(Name, $"player has no '{ComponentName}' component");

        var (display, sanityOk, failReason, value) = ReadOurs(ctx.Reader, compAddr);
        if (!sanityOk) return new TestOutcome.Fail(Name, $"sanity check failed: {failReason} (read: {display})");

        // If POEMCP is reachable, do exact comparison. Otherwise, the sanity check is the verdict.
        var truth = await ctx.Poemcp.EvalAsync(PoemcpExpr, ct);
        if (!truth.Success)
            return new TestOutcome.Pass(Name, $"sanity OK: {display} (POEMCP unavailable, no exact compare)");

        if (TruthMatches(value, truth, out var detail))
            return new TestOutcome.Pass(Name, $"matches POEMCP: {display}");

        return new TestOutcome.Fail(Name, $"mismatch â€” {detail}");
    }
}

public sealed class PlayerLevelTest : ComponentFieldTest
{
    public override string Name => "Player.Level (component +0x1AC)";
    protected override string ComponentName => "Player";
    protected override string PoemcpExpr => "Player.GetComponent<Player>().Level";

    protected override (string, bool, string?, object?) ReadOurs(MemoryReader reader, nint compAddr)
    {
        // PlayerComponentOffsets.Level @ 0x1AC, byte
        if (!reader.TryReadStruct<byte>(compAddr + 0x1AC, out var level))
            return ("(unread)", false, "read failed", null);
        var ok = level >= 1 && level <= 100;
        return ($"level={level}", ok, ok ? null : "level outside 1..100", (int)level);
    }
}

public sealed class PlayerNameTest : ComponentFieldTest
{
    public override string Name => "Player.PlayerName (component +0x168)";
    protected override string ComponentName => "Player";
    protected override string PoemcpExpr => "Player.GetComponent<Player>().PlayerName";

    protected override (string, bool, string?, object?) ReadOurs(MemoryReader reader, nint compAddr)
    {
        if (!reader.TryReadStruct<NativeUtf16Text>(compAddr + 0x168, out var name))
            return ("(unread)", false, "could not read NativeUtf16Text", null);
        var s = ReadInlineOrPointed(reader, compAddr + 0x168, name);
        var ok = !string.IsNullOrEmpty(s) && s.Length <= 32 && s.All(c => c >= ' ' && c <= '~');
        return ($"name='{s}'", ok, ok ? null : "name not a printable ASCII string", s);
    }

    /// <summary>
    /// PoE's NativeUtf16Text uses small-string optimization: if Length &lt;= 7, the chars live inline
    /// in the struct (where Buffer / Reserved8Bytes would be); otherwise Buffer points to the chars.
    /// </summary>
    private static string ReadInlineOrPointed(MemoryReader reader, nint structAddr, NativeUtf16Text txt)
    {
        if (txt.Length <= 0 || txt.Length > 0x4000) return string.Empty;
        var len = (int)txt.Length;
        if (len <= 7)
            return reader.ReadStringUtf16(structAddr, len);
        return reader.ReadStringUtf16(txt.Buffer, len);
    }
}

public sealed class PositionedGridTest : ComponentFieldTest
{
    public override string Name => "Positioned.GridPosition (component +0x294)";
    protected override string ComponentName => "Positioned";
    protected override string PoemcpExpr => "Player.GridPosNum.X + \",\" + Player.GridPosNum.Y";

    protected override (string, bool, string?, object?) ReadOurs(MemoryReader reader, nint compAddr)
    {
        if (!reader.TryReadStruct<int>(compAddr + 0x294, out var x)) return ("(unread)", false, "X read failed", null);
        if (!reader.TryReadStruct<int>(compAddr + 0x298, out var y)) return ("(unread)", false, "Y read failed", null);
        // Grid coords are typically -2000..2000 in playable areas. If we got something massive, the offset is wrong.
        var ok = Math.Abs(x) < 50_000 && Math.Abs(y) < 50_000;
        return ($"grid=({x},{y})", ok, ok ? null : "grid coords look wrong", $"{x},{y}");
    }
}

public sealed class RenderWorldPosTest : ComponentFieldTest
{
    public override string Name => "Render.Pos (component +0x120, Vector3)";
    protected override string ComponentName => "Render";
    protected override string PoemcpExpr => "var p = Player.GetComponent<Render>().Pos; p.X.ToString(\"F1\") + \",\" + p.Y.ToString(\"F1\") + \",\" + p.Z.ToString(\"F1\")";

    protected override (string, bool, string?, object?) ReadOurs(MemoryReader reader, nint compAddr)
    {
        if (!reader.TryReadStruct<float>(compAddr + 0x120, out var x)) return ("(unread)", false, "X failed", null);
        if (!reader.TryReadStruct<float>(compAddr + 0x124, out var y)) return ("(unread)", false, "Y failed", null);
        if (!reader.TryReadStruct<float>(compAddr + 0x128, out var z)) return ("(unread)", false, "Z failed", null);
        var ok = !float.IsNaN(x) && !float.IsNaN(y) && !float.IsNaN(z) &&
                 Math.Abs(x) < 1_000_000 && Math.Abs(y) < 1_000_000 && Math.Abs(z) < 1_000_000;
        return ($"world=({x:F1},{y:F1},{z:F1})", ok, ok ? null : "Vector3 has NaN or huge values", $"{x:F1},{y:F1},{z:F1}");
    }
}

public sealed class LifeCurHpTest : ComponentFieldTest
{
    public override string Name => "Life.Health.Current (component +0x178+0x30)";
    protected override string ComponentName => "Life";
    protected override string PoemcpExpr => "Player.GetComponent<Life>().CurHP";

    protected override (string, bool, string?, object?) ReadOurs(MemoryReader reader, nint compAddr)
    {
        if (!reader.TryReadStruct<VitalStruct>(compAddr + KnownOffsets.LifeComponent.Health, out var h))
            return ("(unread)", false, "health VitalStruct read failed", null);
        var ok = h.LooksValid();
        return ($"HP={h.Current}/{h.Max}", ok, ok ? null : "VitalStruct sanity failed", h.Current);
    }
}
