using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Research.Validation.Tests;

/// <summary>
/// Validates <see cref="ElementGeometry.TryReadRect"/> by walking real panel parents and
/// comparing computed rects to POEMCP's <c>GetClientRect()</c>. This is the load-bearing
/// check for the looter — if Element.Parent or scale-compounding is wrong, target boxes
/// land on the wrong pixel.
///
/// Uses the InventoryPanel and StashElement as anchors because the user can keep them
/// open during validation runs.
/// </summary>
public sealed class InventoryPanelRectOracleTest : ValidationTest
{
    public override string Name => "InventoryPanel rect via parent walk";
    public override string? Group => "UI tree";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(StateKeys.IngameUi, out var uiObj) || uiObj is not nint ingameUi)
            return new TestOutcome.Skip(Name, "IngameUi not resolved");

        if (!ctx.Reader.TryReadStruct<nint>(ingameUi + KnownOffsets.IngameUiElements.InventoryPanel, out var panel)
            || panel == 0)
            return new TestOutcome.Fail(Name, "could not read InventoryPanel pointer");

        return await CompareRect(ctx, "IngameState.IngameUi.InventoryPanel", panel, ct);
    }

    internal static async Task<TestOutcome> CompareRect(TestContext ctx, string poemcpExpr, nint elementAddress, CancellationToken ct)
    {
        var ours = ElementGeometry.TryReadRect(ctx.Reader, elementAddress);
        if (ours is null)
            return new TestOutcome.Fail($"rect {poemcpExpr}", "TryReadRect returned null");

        var truth = await ctx.Poemcp.EvalAsync(
            $$"""
            var e = {{poemcpExpr}};
            var r = e.GetClientRect();
            r.X.ToString("F2") + "," + r.Y.ToString("F2") + "," +
            r.Width.ToString("F2") + "," + r.Height.ToString("F2") + "|" +
            (e.Parent == null ? "null" : e.Parent.Address.ToString("X")) + "|" +
            e.Scale.ToString("F4")
            """, ct);
        if (!truth.Success)
            return new TestOutcome.Skip($"rect {poemcpExpr}", $"POEMCP unavailable: {truth.Error}");

        var parts = truth.AsString().Split('|');
        if (parts.Length != 3)
            return new TestOutcome.Fail($"rect {poemcpExpr}", $"unexpected POEMCP result: {truth.AsString()}");

        var rectParts = parts[0].Split(',');
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var tx = float.Parse(rectParts[0], culture);
        var ty = float.Parse(rectParts[1], culture);
        var tw = float.Parse(rectParts[2], culture);
        var th = float.Parse(rectParts[3], culture);

        var r = ours.Value;
        const float tol = 0.5f;
        if (!Close(r.X, tx, tol) || !Close(r.Y, ty, tol) || !Close(r.Width, tw, tol) || !Close(r.Height, th, tol))
        {
            return new TestOutcome.Fail($"rect {poemcpExpr}",
                $"ours=({r.X:F1},{r.Y:F1} {r.Width:F1}x{r.Height:F1}) " +
                $"POEMCP=({tx:F1},{ty:F1} {tw:F1}x{th:F1}) parent={parts[1]} scale={parts[2]}");
        }

        return new TestOutcome.Pass($"rect {poemcpExpr}",
            $"ours=({r.X:F1},{r.Y:F1} {r.Width:F1}x{r.Height:F1}) parent={parts[1]} scale={parts[2]}");
    }

    private static bool Close(float a, float b, float tol) => Math.Abs(a - b) <= tol;
}

public sealed class StashElementRectOracleTest : ValidationTest
{
    public override string Name => "StashElement rect via parent walk";
    public override string? Group => "UI tree";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(StateKeys.IngameUi, out var uiObj) || uiObj is not nint ingameUi)
            return new TestOutcome.Skip(Name, "IngameUi not resolved");

        if (!ctx.Reader.TryReadStruct<nint>(ingameUi + KnownOffsets.IngameUiElements.StashElement, out var panel)
            || panel == 0)
            return new TestOutcome.Fail(Name, "could not read StashElement pointer");

        return await InventoryPanelRectOracleTest.CompareRect(ctx, "IngameState.IngameUi.StashElement", panel, ct);
    }
}

/// <summary>
/// Walks several specific parents up from InventoryPanel and validates Parent + Position +
/// Size + Scale + Childs.Count at each level. This is what catches a wrong Element.Parent
/// offset — the rect test alone can pass when scale=1 cancels out a missing parent step.
/// </summary>
public sealed class ElementParentChainOracleTest : ValidationTest
{
    public override string Name => "Element parent chain (InventoryPanel up)";
    public override string? Group => "UI tree";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(StateKeys.IngameUi, out var uiObj) || uiObj is not nint ingameUi)
            return new TestOutcome.Skip(Name, "IngameUi not resolved");

        if (!ctx.Reader.TryReadStruct<nint>(ingameUi + KnownOffsets.IngameUiElements.InventoryPanel, out var leaf)
            || leaf == 0)
            return new TestOutcome.Fail(Name, "could not read InventoryPanel");

        var truth = await ctx.Poemcp.EvalAsync(
            """
            ExileCore.PoEMemory.Element e = IngameState.IngameUi.InventoryPanel;
            var sb = new System.Text.StringBuilder();
            var depth = 0;
            while (e != null && depth < 8)
            {
                if (depth > 0) sb.Append(';');
                sb.Append(e.Address.ToString("X")).Append(',')
                  .Append(e.ChildCount).Append(',')
                  .Append(e.Position.X.ToString("F3")).Append(',')
                  .Append(e.Position.Y.ToString("F3")).Append(',')
                  .Append(e.Width.ToString("F3")).Append(',')
                  .Append(e.Height.ToString("F3")).Append(',')
                  .Append(e.Scale.ToString("F4"));
                e = e.Parent;
                depth++;
            }
            sb.ToString()
            """, ct);
        if (!truth.Success)
            return new TestOutcome.Skip(Name, $"POEMCP unavailable: {truth.Error}");

        var truthLevels = truth.AsString().Split(';');

        // Walk same chain via memory.
        var addr = leaf;
        var failures = new List<string>();
        for (var depth = 0; depth < truthLevels.Length; depth++)
        {
            if (!ctx.Reader.TryReadStruct<Element>(addr, out var ours))
            {
                failures.Add($"level {depth}: could not read Element at 0x{addr:X}");
                break;
            }

            var fields = truthLevels[depth].Split(',');
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            var tAddr = (nint)long.Parse(fields[0], System.Globalization.NumberStyles.HexNumber);
            var tChildCount = long.Parse(fields[1]);
            var tPosX = float.Parse(fields[2], culture);
            var tPosY = float.Parse(fields[3], culture);
            var tW = float.Parse(fields[4], culture);
            var tH = float.Parse(fields[5], culture);
            var tScale = float.Parse(fields[6], culture);

            if (addr != tAddr)
                failures.Add($"level {depth} address: ours=0x{addr:X}, POEMCP=0x{tAddr:X}");
            if (ours.Childs.Count != tChildCount)
                failures.Add($"level {depth} childCount: ours={ours.Childs.Count}, POEMCP={tChildCount}");
            if (!Close(ours.Position.X, tPosX) || !Close(ours.Position.Y, tPosY))
                failures.Add($"level {depth} pos: ours=({ours.Position.X:F2},{ours.Position.Y:F2}), POEMCP=({tPosX:F2},{tPosY:F2})");
            if (!Close(ours.Size.X, tW) || !Close(ours.Size.Y, tH))
                failures.Add($"level {depth} size: ours=({ours.Size.X:F2},{ours.Size.Y:F2}), POEMCP=({tW:F2},{tH:F2})");
            if (!Close(ours.Scale, tScale))
                failures.Add($"level {depth} scale: ours={ours.Scale:F4}, POEMCP={tScale:F4}");

            addr = ours.Parent;
            if (addr == 0) break;
        }

        if (failures.Count > 0)
            return new TestOutcome.Fail(Name, string.Join("; ", failures));

        return new TestOutcome.Pass(Name, $"validated {truthLevels.Length} levels of parent chain");
    }

    private static bool Close(float a, float b) => Math.Abs(a - b) < 0.05f;
}
