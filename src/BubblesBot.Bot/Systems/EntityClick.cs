using BubblesBot.Bot.Behaviors;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Systems;

/// <summary>
/// Shared "where do I click to interact with this world entity" math. Projects the entity's
/// Render bounds-center to absolute screen coords; falls back to its grid position projected
/// at the player's Z when the bounds projection lands off-screen. Same two-stage approach
/// <c>MapDeviceSystem</c> / <c>InteractWorldEntity</c> use — extracted so the stacked-deck
/// systems (leave-map, stash deposit) don't each re-implement it.
/// </summary>
public static class EntityClick
{
    public static (int X, int Y)? ResolveScreenPoint(BehaviorContext ctx, EntityCache.Entry target)
    {
        var cam = ctx.Snapshot.Camera;
        if (!cam.IsValid || ctx.Live is null) return null;
        var w = ctx.Snapshot.Window;
        bool OnScreen((float X, float Y)? p) => p is { } v && v.X >= 0 && v.Y >= 0 && v.X < w.Width && v.Y < w.Height;

        (float X, float Y)? center = null;
        if (target.RenderCompAddr != 0)
        {
            var reader = ctx.Snapshot.Reader;
            if (reader.TryReadStruct<Vector3>(target.RenderCompAddr + KnownOffsets.RenderComponent.Pos, out var rPos)
             && reader.TryReadStruct<Vector3>(target.RenderCompAddr + KnownOffsets.RenderComponent.Bounds, out var rBounds))
            {
                var centerWorld = new Vector3
                {
                    X = rPos.X + rBounds.X * 0.5f,
                    Y = rPos.Y + rBounds.Y * 0.5f,
                    Z = rPos.Z + rBounds.Z * 0.5f,
                };
                center = cam.WorldToScreen(centerWorld);
            }
        }
        if (!OnScreen(center))
        {
            center = cam.GridToScreenAtPlayerZ(target.GridPosition, ctx.Live.Value.WorldPosition.Z);
            if (!OnScreen(center)) return null;
        }
        var (sx, sy) = w.ToScreen((int)center!.Value.X, (int)center.Value.Y);
        return (sx, sy);
    }
}
