using BubblesBot.Core.Game;
using BubblesBot.Research.Probing;
using BubblesBot.Research.Probing.Toolkit;

namespace BubblesBot.Research.Probes.Camera;

/// <summary>Camera ZFar — the far plane used by the world-to-screen projection.</summary>
public sealed class CameraZFarProbe : IProbe
{
    public string Name => "camera.zfar";
    public string Group => "camera";
    public string Description => "Camera ZFar matches baseline.";
    public IReadOnlyList<string> RequiredFacts => ["camera.zfar"];

    public ProbeResult Validate(ProbeContext ctx)
    {
        var cam = ctx.Chain.Camera;
        if (cam == 0) return ProbeResult.Fail("Camera pointer null");
        if (!ctx.Reader.TryReadStruct<float>(cam + KnownOffsets.Camera.ZFar, out var z))
            return ProbeResult.Fail($"unreadable Camera.ZFar at +0x{KnownOffsets.Camera.ZFar:X}");
        return Check.Float(ctx, "camera.zfar", z, "Camera.ZFar", epsilon: 1.0f);
    }

    public ProbeResult Discover(ProbeContext ctx)
    {
        var cam = ctx.Chain.Camera;
        if (cam == 0 || !ctx.Facts.TryGetFloat("camera.zfar", out var target)) return ProbeResult.Found("Camera.ZFar", []);
        var cands = MemScan.WindowFloat(ctx.Reader, cam, 0x600, target, epsilon: 1.0f)
            .Select(o => new OffsetCandidate(o, $"(={target:0.###})"));
        return ProbeResult.Found("Camera.ZFar", cands);
    }
}
