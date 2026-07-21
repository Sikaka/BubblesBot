using BubblesBot.Core.Game;

namespace BubblesBot.Research.Validation.Tests;

/// <summary>
/// Validates Camera offsets by walking IngameState -> Camera and comparing
/// direct reads/projection math against POEMCP.
/// </summary>
public sealed class CameraAddressTest : ValidationTest
{
    public override string Name => "Camera - read from IngameState (+0x270)";
    public override string? Group => "Camera";

    public override Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue(StateKeys.IngameState, out var sObj) || sObj is not nint ingameState)
            return Task.FromResult<TestOutcome>(new TestOutcome.Skip(Name, "IngameState not resolved"));

        if (!ctx.Reader.TryReadStruct<nint>(ingameState + KnownOffsets.IngameState.Camera, out var cameraAddr))
            return Task.FromResult<TestOutcome>(new TestOutcome.Fail(Name, "could not read Camera pointer"));
        if (cameraAddr == 0)
            return Task.FromResult<TestOutcome>(new TestOutcome.Fail(Name, "Camera is null"));

        ctx.State["addr.Camera"] = cameraAddr;
        return Task.FromResult<TestOutcome>(new TestOutcome.Pass(Name, $"Camera @ 0x{cameraAddr:X16}"));
    }
}

public sealed class CameraSizeTest : ValidationTest
{
    public override string Name => "Camera.Width/Height (+0x318/+0x31C)";
    public override string? Group => "Camera";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue("addr.Camera", out var cObj) || cObj is not nint cameraAddr)
            return new TestOutcome.Skip(Name, "Camera address not resolved");

        if (!ctx.Reader.TryReadStruct<int>(cameraAddr + KnownOffsets.Camera.Width, out var width))
            return new TestOutcome.Fail(Name, "could not read Camera.Width");
        if (!ctx.Reader.TryReadStruct<int>(cameraAddr + KnownOffsets.Camera.Height, out var height))
            return new TestOutcome.Fail(Name, "could not read Camera.Height");

        var sane = width >= 640 && width <= 8000 && height >= 480 && height <= 6000;
        if (!sane) return new TestOutcome.Fail(Name, $"resolution {width}x{height} out of range");

        var truth = await ctx.Poemcp.EvalAsync("IngameState.Camera.Width + \"x\" + IngameState.Camera.Height", ct);
        if (!truth.Success) return new TestOutcome.Pass(Name, $"sanity OK: {width}x{height} (POEMCP unavailable)");

        var parts = truth.AsString().Split('x');
        if (parts.Length == 2 && int.TryParse(parts[0], out var tw) && int.TryParse(parts[1], out var th))
        {
            if (width != tw || height != th)
                return new TestOutcome.Fail(Name, $"ours {width}x{height} != truth {tw}x{th}");
        }

        return new TestOutcome.Pass(Name, $"matches POEMCP: {width}x{height}");
    }
}

public sealed class CameraWorldToScreenTest : ValidationTest
{
    public override string Name => "Camera.WorldToScreen matrix (+0x1E8)";
    public override string? Group => "Camera";

    public override async Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue("addr.Camera", out var cObj) || cObj is not nint cameraAddr)
            return new TestOutcome.Skip(Name, "Camera address not resolved");

        if (!ctx.Reader.TryReadStruct<int>(cameraAddr + KnownOffsets.Camera.Width, out var width)
            || !ctx.Reader.TryReadStruct<int>(cameraAddr + KnownOffsets.Camera.Height, out var height))
            return new TestOutcome.Fail(Name, "could not read camera dimensions");

        var truth = await ctx.Poemcp.EvalAsync(
            """
            var p = Player.GetComponent<Render>().Pos;
            var s = IngameState.Camera.WorldToScreen(p);
            p.X.ToString("F3") + "|" + p.Y.ToString("F3") + "|" + p.Z.ToString("F3") + "|" +
            s.X.ToString("F3") + "|" + s.Y.ToString("F3")
            """, ct);
        if (!truth.Success)
            return new TestOutcome.Skip(Name, "POEMCP unavailable");

        var parts = truth.AsString().Split('|');
        if (parts.Length != 5)
            return new TestOutcome.Fail(Name, $"unexpected POEMCP result: {truth.AsString()}");

        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var x = float.Parse(parts[0], culture);
        var y = float.Parse(parts[1], culture);
        var z = float.Parse(parts[2], culture);
        var expectedX = float.Parse(parts[3], culture);
        var expectedY = float.Parse(parts[4], culture);

        Span<byte> matrixBytes = stackalloc byte[64];
        if (ctx.Reader.TryReadBytes(cameraAddr + KnownOffsets.Camera.MatrixBytes, matrixBytes) != 64)
            return new TestOutcome.Fail(Name, "could not read camera matrix");

        var projected = Project(matrixBytes, x, y, z, width, height);
        if (projected is not { } p)
            return new TestOutcome.Fail(Name, "matrix projection produced invalid coordinates");

        var dx = Math.Abs(p.X - expectedX);
        var dy = Math.Abs(p.Y - expectedY);
        if (dx > 2 || dy > 2)
            return new TestOutcome.Fail(Name, $"ours ({p.X:F2},{p.Y:F2}) != POEMCP ({expectedX:F2},{expectedY:F2}), delta=({dx:F2},{dy:F2})");

        return new TestOutcome.Pass(Name, $"matches POEMCP: ({p.X:F1},{p.Y:F1})");
    }

    private static (float X, float Y)? Project(ReadOnlySpan<byte> bytes, float x, float y, float z, int width, int height)
    {
        Span<float> m = stackalloc float[16];
        for (var i = 0; i < 16; i++)
        {
            m[i] = BitConverter.ToSingle(bytes[(i * 4)..(i * 4 + 4)]);
            if (!float.IsFinite(m[i]) || Math.Abs(m[i]) > 1_000_000) return null;
        }

        var clipX = x * m[0] + y * m[4] + z * m[8] + m[12];
        var clipY = x * m[1] + y * m[5] + z * m[9] + m[13];
        var clipW = x * m[3] + y * m[7] + z * m[11] + m[15];
        if (!float.IsFinite(clipW) || Math.Abs(clipW) < 0.0001f) return null;

        var ndcX = clipX / clipW;
        var ndcY = clipY / clipW;
        if (!float.IsFinite(ndcX) || !float.IsFinite(ndcY)) return null;
        return ((ndcX + 1.0f) * width * 0.5f, (1.0f - ndcY) * height * 0.5f);
    }
}

public sealed class CameraZoomTest : ValidationTest
{
    public override string Name => "Camera.ActualZoomLevel (+0x4A8) / DesiredZoomLevel (+0x4B0)";
    public override string? Group => "Camera";

    public override Task<TestOutcome> RunAsync(TestContext ctx, CancellationToken ct)
    {
        if (!ctx.State.TryGetValue("addr.Camera", out var cObj) || cObj is not nint cameraAddr)
            return Task.FromResult<TestOutcome>(new TestOutcome.Skip(Name, "Camera address not resolved"));

        if (!ctx.Reader.TryReadStruct<float>(cameraAddr + KnownOffsets.Camera.ActualZoomLevel, out var actual))
            return Task.FromResult<TestOutcome>(new TestOutcome.Fail(Name, "could not read ActualZoomLevel"));
        if (!ctx.Reader.TryReadStruct<float>(cameraAddr + KnownOffsets.Camera.DesiredZoomLevel, out var desired))
            return Task.FromResult<TestOutcome>(new TestOutcome.Fail(Name, "could not read DesiredZoomLevel"));

        var sane = actual > 0 && actual < 50 && desired > 0 && desired < 50;
        if (!sane) return Task.FromResult<TestOutcome>(new TestOutcome.Fail(Name, $"zoom actual={actual} desired={desired} out of range"));

        return Task.FromResult<TestOutcome>(new TestOutcome.Pass(Name, $"sanity OK: actualZoom={actual:F2} desiredZoom={desired:F2}"));
    }
}
