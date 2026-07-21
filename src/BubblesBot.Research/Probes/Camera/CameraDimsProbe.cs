using BubblesBot.Core.Game;
using BubblesBot.Research.Probing;
using BubblesBot.Research.Probing.Toolkit;

namespace BubblesBot.Research.Probes.Camera;

/// <summary>
/// Camera render width/height should equal the game's client resolution. Cheap, stable canary for
/// the Camera struct (the same object whose matrix drives world-to-screen projection).
/// </summary>
public sealed class CameraDimsProbe : IProbe
{
    public string Name => "camera.dims";
    public string Group => "camera";
    public string Description => "Camera Width/Height match baseline resolution.";
    public IReadOnlyList<string> RequiredFacts => ["camera.width", "camera.height"];

    public ProbeResult Validate(ProbeContext ctx)
    {
        var cam = ctx.Chain.Camera;
        if (cam == 0) return ProbeResult.Fail("Camera pointer null");

        var w = ctx.Reader.TryReadStruct<int>(cam + KnownOffsets.Camera.Width, out var width)
            ? Check.Int(ctx, "camera.width", width, "Camera.Width")
            : ProbeResult.Fail($"unreadable Camera.Width at +0x{KnownOffsets.Camera.Width:X}");

        var h = ctx.Reader.TryReadStruct<int>(cam + KnownOffsets.Camera.Height, out var height)
            ? Check.Int(ctx, "camera.height", height, "Camera.Height")
            : ProbeResult.Fail($"unreadable Camera.Height at +0x{KnownOffsets.Camera.Height:X}");

        return ProbeResult.Combine(w, h);
    }

    public ProbeResult Discover(ProbeContext ctx)
    {
        var cam = ctx.Chain.Camera;
        if (cam == 0) return ProbeResult.Found("Camera.Width", []);
        if (!TryTarget(ctx, "camera.width", out var width)) return ProbeResult.Found("Camera.Width", []);

        // Width and height sit adjacent; report width offsets (height = width+4).
        var cands = MemScan.WindowInt32(ctx.Reader, cam, window: 0x600, width)
            .Select(o => new OffsetCandidate(o, $"Width(={width}); expect Height at +0x{o + 4:X}"));
        return ProbeResult.Found("Camera.Width", cands);
    }

    private static bool TryTarget(ProbeContext ctx, string key, out int value)
    {
        if (ctx.Oracle.IsAvailable && ctx.Oracle.TryGetValue(key, out var os) && int.TryParse(os, out value)) return true;
        return ctx.Facts.TryGetInt(key, out value);
    }
}
