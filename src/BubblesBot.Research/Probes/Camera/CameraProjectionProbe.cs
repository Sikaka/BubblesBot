using BubblesBot.Core.Game;
using BubblesBot.Research.Probing;

namespace BubblesBot.Research.Probes.Camera;

/// <summary>
/// The world-to-screen projection matrix — the bot's targeting backbone. Projects the player's own
/// render position through the matrix at Camera.MatrixBytes and, when the oracle is present, checks
/// it lands within 2px of ExileAPI's WorldToScreen. Without an oracle, asserts the projection is
/// finite and on/near screen. Migrated from CameraWorldToScreenTest.
/// </summary>
public sealed class CameraProjectionProbe : IProbe
{
    public string Name => "camera.projection";
    public string Group => "camera";
    public string Description => "Camera matrix projects player world pos to the correct screen point.";
    public IReadOnlyList<string> RequiredFacts => [];

    public ProbeResult Validate(ProbeContext ctx)
    {
        var cam = ctx.Chain.Camera;
        var render = ctx.Chain.PlayerComponent("Render");
        if (cam == 0 || render == 0) return ProbeResult.Fail("Camera or Render component null");

        if (!ctx.Reader.TryReadStruct<Vector3>(render + KnownOffsets.RenderComponent.Pos, out var pos))
            return ProbeResult.Fail("Render.Pos unreadable");
        if (!ctx.Reader.TryReadStruct<int>(cam + KnownOffsets.Camera.Width, out var w)
            || !ctx.Reader.TryReadStruct<int>(cam + KnownOffsets.Camera.Height, out var h))
            return ProbeResult.Fail("Camera dims unreadable");

        Span<byte> matrix = stackalloc byte[64];
        if (ctx.Reader.TryReadBytes(cam + KnownOffsets.Camera.MatrixBytes, matrix) != 64)
            return ProbeResult.Fail($"matrix unreadable at Camera+0x{KnownOffsets.Camera.MatrixBytes:X}");

        var p = Project(matrix, pos, w, h);
        if (p is not { } screen)
            return ProbeResult.Fail("projection produced non-finite result (matrix offset wrong?)");

        if (ctx.Oracle.IsAvailable
            && ctx.Oracle.TryGetValue("camera.w2s.x", out var sx) && float.TryParse(sx, out var ox)
            && ctx.Oracle.TryGetValue("camera.w2s.y", out var sy) && float.TryParse(sy, out var oy))
        {
            var dx = Math.Abs(screen.X - ox);
            var dy = Math.Abs(screen.Y - oy);
            return dx <= 2 && dy <= 2
                ? ProbeResult.Pass($"projected ({screen.X:0},{screen.Y:0}) within 2px of oracle ({ox:0},{oy:0})")
                : ProbeResult.Fail($"projected ({screen.X:0},{screen.Y:0}) vs oracle ({ox:0},{oy:0}) dx={dx:0.#} dy={dy:0.#}");
        }

        // No oracle: the player is essentially always on-screen, so a sane projection lands within
        // a generous margin of the viewport.
        return screen.X > -w && screen.X < 2 * w && screen.Y > -h && screen.Y < 2 * h
            ? ProbeResult.Pass($"projected player to ({screen.X:0},{screen.Y:0}) in {w}x{h} (no oracle)")
            : ProbeResult.Fail($"projected ({screen.X:0},{screen.Y:0}) far off {w}x{h} viewport");
    }

    public ProbeResult Discover(ProbeContext ctx)
        // Matrix discovery is a 64-byte-window projection search (see Research --inspect-camera-layout);
        // not a single-offset scan.
        => ProbeResult.Found("Camera.MatrixBytes", []);

    private static (float X, float Y)? Project(ReadOnlySpan<byte> bytes, Vector3 pos, int width, int height)
    {
        Span<float> m = stackalloc float[16];
        for (var i = 0; i < 16; i++)
        {
            m[i] = BitConverter.ToSingle(bytes[(i * 4)..(i * 4 + 4)]);
            if (!float.IsFinite(m[i]) || Math.Abs(m[i]) > 1_000_000) return null;
        }
        var clipX = pos.X * m[0] + pos.Y * m[4] + pos.Z * m[8] + m[12];
        var clipY = pos.X * m[1] + pos.Y * m[5] + pos.Z * m[9] + m[13];
        var clipW = pos.X * m[3] + pos.Y * m[7] + pos.Z * m[11] + m[15];
        if (!float.IsFinite(clipW) || Math.Abs(clipW) < 0.0001f) return null;
        var ndcX = clipX / clipW;
        var ndcY = clipY / clipW;
        if (!float.IsFinite(ndcX) || !float.IsFinite(ndcY)) return null;
        return ((ndcX + 1.0f) * width * 0.5f, (1.0f - ndcY) * height * 0.5f);
    }
}
