using BubblesBot.Core.Game;

namespace BubblesBot.Bot.Systems;

public sealed class MovementProgressWatchdog
{
    private readonly float _minimumProgress;
    private readonly TimeSpan _stuckAfter;
    private Vector2i _lastCell;
    private TimeSpan _lastProgressAt;
    private bool _initialized;

    public MovementProgressWatchdog(float minimumProgress, TimeSpan stuckAfter)
    {
        _minimumProgress = minimumProgress;
        _stuckAfter = stuckAfter;
    }

    public bool Observe(Vector2i cell, TimeSpan now)
    {
        if (!_initialized || Distance(cell, _lastCell) > _minimumProgress)
        {
            MarkProgress(cell, now);
            return false;
        }
        return now - _lastProgressAt > _stuckAfter;
    }

    public void MarkProgress(Vector2i cell, TimeSpan now)
    {
        _lastCell = cell;
        _lastProgressAt = now;
        _initialized = true;
    }

    public void Reset()
    {
        _initialized = false;
        _lastProgressAt = TimeSpan.Zero;
    }

    private static float Distance(Vector2i a, Vector2i b)
    {
        var dx = (float)(a.X - b.X);
        var dy = (float)(a.Y - b.Y);
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
