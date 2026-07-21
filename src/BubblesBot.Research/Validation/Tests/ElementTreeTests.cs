using BubblesBot.Core.Game;

namespace BubblesBot.Research.Validation.Tests;

public sealed class UiRootElementOracleTest : ValidationTest
{
    public override string Name => "UIRoot generic Element fields";
    public override string? Group => "UI tree";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(StateKeys.IngameState, out var stateObj) || stateObj is not nint ingameState)
            return new TestOutcome.Skip(Name, "IngameState not resolved");

        if (!ctx.Reader.TryReadStruct<nint>(ingameState + KnownOffsets.IngameState.UIRoot, out var rootAddress))
            return new TestOutcome.Fail(Name, "could not read UIRoot pointer");

        var snapshot = ElementReader.TryReadSnapshot(ctx.Reader, rootAddress);
        if (snapshot is null)
            return new TestOutcome.Fail(Name, $"could not read UIRoot element at 0x{rootAddress:X}");

        var truth = await ctx.Poemcp.EvalAsync(
            """
            var e = IngameState.UIRoot;
            e.Address.ToString("X") + "|" +
            e.IsValid + "|" + e.IsVisibleLocal + "|" +
            e.ChildCount + "|" +
            e.Position.X.ToString("F3") + "," + e.Position.Y.ToString("F3") + "|" +
            e.Width.ToString("F3") + "," + e.Height.ToString("F3") + "|" +
            e.Scale.ToString("F3")
            """, ct);
        if (!truth.Success)
            return new TestOutcome.Skip(Name, $"POEMCP unavailable: {truth.Error}");

        var parts = truth.AsString().Split('|');
        if (parts.Length != 7)
            return new TestOutcome.Fail(Name, $"unexpected POEMCP result: {truth.AsString()}");

        var truthAddress = (nint)long.Parse(parts[0], System.Globalization.NumberStyles.HexNumber);
        var truthValid = bool.Parse(parts[1]);
        var truthVisibleLocal = bool.Parse(parts[2]);
        var truthChildCount = long.Parse(parts[3]);
        var truthPos = ParseFloatPair(parts[4]);
        var truthSize = ParseFloatPair(parts[5]);
        var truthScale = float.Parse(parts[6], System.Globalization.CultureInfo.InvariantCulture);

        if (snapshot.Address != truthAddress)
            return new TestOutcome.Fail(Name, $"address mismatch ours=0x{snapshot.Address:X}, POEMCP=0x{truthAddress:X}");
        if (snapshot.IsValid != truthValid)
            return new TestOutcome.Fail(Name, $"IsValid mismatch ours={snapshot.IsValid}, POEMCP={truthValid}");
        if (snapshot.IsVisibleLocal != truthVisibleLocal)
            return new TestOutcome.Fail(Name, $"IsVisibleLocal mismatch ours={snapshot.IsVisibleLocal}, POEMCP={truthVisibleLocal}");
        if (snapshot.Children.Count != truthChildCount)
            return new TestOutcome.Fail(Name, $"ChildCount mismatch ours={snapshot.Children.Count}, POEMCP={truthChildCount}");
        if (!Close(snapshot.Position.X, truthPos.X) || !Close(snapshot.Position.Y, truthPos.Y))
            return new TestOutcome.Fail(Name, $"Position mismatch ours=({snapshot.Position.X:F3},{snapshot.Position.Y:F3}), POEMCP={parts[4]}");
        if (!Close(snapshot.Size.X, truthSize.X) || !Close(snapshot.Size.Y, truthSize.Y))
            return new TestOutcome.Fail(Name, $"Size mismatch ours=({snapshot.Size.X:F3},{snapshot.Size.Y:F3}), POEMCP={parts[5]}");
        if (!Close(snapshot.Scale, truthScale))
            return new TestOutcome.Fail(Name, $"Scale mismatch ours={snapshot.Scale:F3}, POEMCP={truthScale:F3}");

        ctx.State["addr.UIRoot"] = rootAddress;
        return new TestOutcome.Pass(Name, $"root=0x{rootAddress:X16}, children={snapshot.Children.Count}, size=({snapshot.Size.X:F0},{snapshot.Size.Y:F0}), scale={snapshot.Scale:F3}");
    }

    private static Vector2 ParseFloatPair(string value)
    {
        var parts = value.Split(',', 2);
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        return new Vector2 { X = float.Parse(parts[0], culture), Y = float.Parse(parts[1], culture) };
    }

    private static bool Close(float a, float b) => Math.Abs(a - b) < 0.01f;
}
