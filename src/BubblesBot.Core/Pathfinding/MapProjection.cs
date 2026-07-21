using BubblesBot.Core.Game;

namespace BubblesBot.Core.Pathfinding;

/// <summary>
/// Isometric projection from grid coordinates to map-screen coordinates. PoE renders the
/// map with a 38.7° camera tilt and a player-centered frame; the equation matches the
/// open-source Radar plugin so overlays line up with the in-game player blip.
///
/// <para>Convention: <c>delta</c> is the cell offset from the player to the target
/// (target.grid - player.grid). The result is added to the map center.</para>
/// </summary>
public static class MapProjection
{
    private const double CameraAngleRad = 38.7 * Math.PI / 180.0;
    public static readonly float CameraCos = (float)Math.Cos(CameraAngleRad);
    public static readonly float CameraSin = (float)Math.Sin(CameraAngleRad);

    /// <summary>
    /// Convert a grid delta (target - player) into the equivalent screen-space delta on the
    /// map at the given <paramref name="mapScale"/>. Optional <paramref name="deltaWorldZ"/>
    /// adds elevation; pass 0 for flat terrain (acceptable until height data is wired up).
    /// </summary>
    public static Vector2 GridDeltaToMapDelta(Vector2 delta, float mapScale, float deltaWorldZ = 0f)
    {
        var dz = deltaWorldZ / GridConstants.GridToWorld;
        return new Vector2
        {
            X = mapScale * (delta.X - delta.Y) * CameraCos,
            Y = mapScale * (dz - (delta.X + delta.Y)) * CameraSin,
        };
    }

    /// <summary>
    /// Project a grid cell to the map by adding its delta-from-player to the map center.
    /// Both <paramref name="mapCenter"/> and the result are in window-relative pixels.
    /// </summary>
    public static Vector2 GridToMapPoint(
        Vector2 gridCell,
        Vector2 playerGrid,
        Vector2 mapCenter,
        float   mapScale,
        float   deltaWorldZ = 0f)
    {
        var d = new Vector2 { X = gridCell.X - playerGrid.X, Y = gridCell.Y - playerGrid.Y };
        var md = GridDeltaToMapDelta(d, mapScale, deltaWorldZ);
        return new Vector2 { X = mapCenter.X + md.X, Y = mapCenter.Y + md.Y };
    }
}
