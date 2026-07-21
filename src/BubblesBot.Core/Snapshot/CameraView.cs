using BubblesBot.Core.Game;

namespace BubblesBot.Core.Snapshot;

/// <summary>
/// Projects 3D world coordinates into 2D window-relative screen coordinates using PoE's
/// camera matrix. Validated against POEMCP's <c>Camera.WorldToScreen</c> in
/// <c>CameraWorldToScreenTest</c> — projection is accurate to ±2 px.
///
/// <para>Read once per snapshot — the matrix is stable for the snapshot's lifetime. Window
/// width/height come from <see cref="WindowInfo"/>, but we use the camera-reported width/height
/// (which may differ during borderless transitions) to keep the math consistent with what
/// PoE itself uses.</para>
/// </summary>
public sealed class CameraView
{
    private readonly float[] _m = new float[16];
    private readonly int _width;
    private readonly int _height;

    public bool IsValid { get; }
    public int  Width   => _width;
    public int  Height  => _height;

    public CameraView(MemoryReader reader, nint ingameStateAddress)
    {
        if (!reader.TryReadStruct<nint>(ingameStateAddress + KnownOffsets.IngameState.Camera, out var cameraAddr)
            || cameraAddr == 0)
            return;

        if (!reader.TryReadStruct<int>(cameraAddr + KnownOffsets.Camera.Width,  out _width)
         || !reader.TryReadStruct<int>(cameraAddr + KnownOffsets.Camera.Height, out _height))
            return;

        Span<byte> bytes = stackalloc byte[64];
        if (reader.TryReadBytes(cameraAddr + KnownOffsets.Camera.MatrixBytes, bytes) != 64)
            return;

        for (var i = 0; i < 16; i++)
        {
            _m[i] = BitConverter.ToSingle(bytes.Slice(i * 4, 4));
            if (!float.IsFinite(_m[i]) || Math.Abs(_m[i]) > 1_000_000) return;
        }
        IsValid = _width >= 320 && _height >= 240;
    }

    /// <summary>Project a world-space point to window-relative pixels. Null if behind camera.</summary>
    public (float X, float Y)? WorldToScreen(Vector3 world)
        => WorldToScreen(world.X, world.Y, world.Z);

    public (float X, float Y)? WorldToScreen(float x, float y, float z)
    {
        if (!IsValid) return null;
        var clipX = x * _m[0] + y * _m[4] + z * _m[8]  + _m[12];
        var clipY = x * _m[1] + y * _m[5] + z * _m[9]  + _m[13];
        var clipW = x * _m[3] + y * _m[7] + z * _m[11] + _m[15];
        if (!float.IsFinite(clipW) || Math.Abs(clipW) < 0.0001f) return null;
        var ndcX = clipX / clipW;
        var ndcY = clipY / clipW;
        if (!float.IsFinite(ndcX) || !float.IsFinite(ndcY)) return null;
        return ((ndcX + 1.0f) * _width * 0.5f, (1.0f - ndcY) * _height * 0.5f);
    }

    /// <summary>
    /// Convenience: project a grid cell to screen at the player's elevation. Movement code
    /// only ever needs to project nearby cells, so reusing the player's Z is accurate enough.
    /// Returns null if projection failed or the result is off-screen.
    /// </summary>
    public (float X, float Y)? GridToScreenAtPlayerZ(Vector2i grid, float playerZ)
    {
        const float gridToWorld = 10.88f;
        return WorldToScreen(grid.X * gridToWorld, grid.Y * gridToWorld, playerZ);
    }
}
