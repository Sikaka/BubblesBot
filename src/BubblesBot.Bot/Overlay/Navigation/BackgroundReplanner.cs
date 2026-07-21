using BubblesBot.Core.Game;
using BubblesBot.Core.Pathfinding;

namespace BubblesBot.Bot.Overlay.Navigation;

/// <summary>
/// Owns a single worker thread that builds <see cref="DistanceField"/>s off the tick threads. Only
/// the most recent request is kept (coalesced) — guidance tracks one primary target at a time, and a
/// distance field covers every start cell, so once built the tick thread re-paths for free. The
/// completed field is published via a volatile reference the tick thread reads lock-free.
///
/// <para>Thread-safety: the <see cref="ICellReader"/> handed in a request is used only by the worker
/// thread; callers must pass a reader they are not touching elsewhere (the world thread builds a
/// dedicated one in Phase C).</para>
/// </summary>
public sealed class BackgroundReplanner : IDisposable
{
    public readonly record struct Request(long Key, Vector2i Target, ICellReader Grid);

    private readonly Thread _worker;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _signal = new(false);
    private Request? _pending;
    private volatile bool _disposed;

    private volatile DistanceField? _current;
    private long _currentKey;
    private Vector2i _currentTarget;

    public DistanceField? Current => _current;
    public long CurrentKey => Interlocked.Read(ref _currentKey);
    public Vector2i CurrentTarget => _currentTarget;

    public BackgroundReplanner()
    {
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "GuidanceReplanner" };
        _worker.Start();
    }

    /// <summary>Queue (coalescing) a field build. A newer request replaces any not-yet-started one.</summary>
    public void Submit(Request request)
    {
        lock (_gate)
        {
            _pending = request;
            _signal.Set();
        }
    }

    private void WorkerLoop()
    {
        while (!_disposed)
        {
            _signal.Wait();
            if (_disposed) return;

            Request req;
            lock (_gate)
            {
                if (_pending is null) { _signal.Reset(); continue; }
                req = _pending.Value;
                _pending = null;
                _signal.Reset();
            }

            DistanceField field;
            try { field = DistanceField.Build(req.Grid, req.Target); }
            catch { continue; } // a bad grid/target must not kill the worker

            _current = field;
            _currentTarget = req.Target;
            Interlocked.Exchange(ref _currentKey, req.Key);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _signal.Set();
        if (_worker.IsAlive) _worker.Join(TimeSpan.FromMilliseconds(250));
        _signal.Dispose();
    }
}
