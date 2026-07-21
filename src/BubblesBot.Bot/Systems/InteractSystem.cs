using BubblesBot.Bot.Input;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Systems;

/// <summary>
/// Generic "click an entity label and verify" facade. Used by loot, NPCs, area transitions,
/// stash, map device — anything where we click a ground/world label and then need to confirm
/// PoE acted on the click (label vanished, panel opened, area changed).
///
/// <para><b>Why a system, not a one-off in each mode.</b> Click-then-verify is the same shape
/// every time — only the post-condition predicate differs. Centralizing here means the retry
/// policy, gate handling, and click-coord math live in one place.</para>
///
/// <para>Returns an <see cref="InteractTicket"/> the caller can poll. Behaviors layered on
/// top translate poll state into <see cref="Behaviors.BehaviorStatus"/>.</para>
/// </summary>
public sealed class InteractSystem
{
    private InteractTicket? _current;

    public InteractTicket? Current => _current;
    public bool IsBusy => _current is { IsResolved: false };

    /// <summary>
    /// Begin an interaction targeting <paramref name="label"/>. The click lands on an
    /// UNOCCLUDED point of the label rect — overlapping labels (stacked drops, the on-ground
    /// Ritual Rewards button) routinely cover the center, and a center click then hits the
    /// wrong element (live 2026-07-15: stacked-deck pile behind the Rituals button). Returns
    /// null when the gate is busy, coords are invalid, or the label is fully covered.
    /// </summary>
    public InteractTicket? Begin(GroundLabelView label, IInputRouter input, WindowInfo window,
        Func<bool> verify, string description, int timeoutMs = 1500,
        IReadOnlyList<ElementGeometry.Rect>? avoid = null)
    {
        if (IsBusy) return null;
        if (label.LabelRect is not { } rect) return null;

        if (FindUncoveredPoint(rect, avoid) is not { } point) return null;
        var (sx, sy) = window.ToScreen(point.X, point.Y);
        var ticket = input.Click(sx, sy, ClickIntent.InteractWorld, description,
            expectResolved: verify, timeoutMs: timeoutMs);
        if (!ticket.Accepted) return null;

        _current = new InteractTicket(label.LabelAddress, description, ticket);
        return _current;
    }

    // Center first; then vertical offsets (labels stack in vertical lists, so up/down
    // dodges most overlap); then horizontal and corner fractions as a last resort.
    private static readonly (float FX, float FY)[] ClickFractions =
    {
        (0.5f, 0.5f), (0.5f, 0.28f), (0.5f, 0.72f),
        (0.28f, 0.5f), (0.72f, 0.5f),
        (0.28f, 0.28f), (0.72f, 0.28f), (0.28f, 0.72f), (0.72f, 0.72f),
        (0.12f, 0.5f), (0.88f, 0.5f),
    };

    /// <summary>First candidate point inside <paramref name="target"/> covered by none of
    /// <paramref name="occluders"/>; null when every candidate is covered.</summary>
    public static (float X, float Y)? FindUncoveredPoint(
        ElementGeometry.Rect target, IReadOnlyList<ElementGeometry.Rect>? occluders)
    {
        if (occluders is null || occluders.Count == 0)
            return (target.CenterX, target.CenterY);
        foreach (var (fx, fy) in ClickFractions)
        {
            var x = target.X + target.Width * fx;
            var y = target.Y + target.Height * fy;
            var covered = false;
            foreach (var o in occluders)
                if (o.Contains(x, y)) { covered = true; break; }
            if (!covered) return (x, y);
        }
        return null;
    }

    /// <summary>Clear the current ticket if it's resolved. Call from the owning behavior each tick.</summary>
    public void Tick()
    {
        if (_current is { IsResolved: true }) _current = null;
    }

    public void Cancel() => _current = null;
}

/// <summary>
/// Outstanding interaction. Wraps an <see cref="InputTicket"/> with the targeted label
/// address so callers can detect "the label vanished but the gate timed out" mid-flight.
/// </summary>
public sealed class InteractTicket
{
    public InteractTicket(nint labelAddress, string description, InputTicket input)
    {
        LabelAddress = labelAddress;
        Description  = description;
        Input        = input;
    }

    public nint        LabelAddress { get; }
    public string      Description  { get; }
    public InputTicket Input        { get; }
    public bool        IsResolved   => Input.IsResolved;
}
