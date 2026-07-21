using BubblesBot.Bot.Input;
using BubblesBot.Bot.Systems;

namespace BubblesBot.Bot.Behaviors.Interact;

/// <summary>
/// Keeps the UI in the state a mechanic expects: closes any panel open beyond the declared
/// <c>expected</c> set by pressing Escape, re-checking each tick until the open set is clean.
/// This is the accidental-click / stray-panel recovery — a misclick that opened a vendor, a
/// level-up popup, a dialog left open — built on <c>OpenPanelsView.OpenExcept</c>.
///
/// <para>Semantics as a tree leaf:
/// <list type="bullet">
///   <item><b>Success</b> — nothing unexpected is open (the common case; cheap no-op).</item>
///   <item><b>Running</b> — something unexpected is open; pressed Escape this tick, will re-check.</item>
///   <item><b>Failure</b> — still not clean after <see cref="_maxPresses"/> Escapes (a panel that
///         Escape can't close — surfaced so the caller can escalate/stop).</item>
/// </list>
/// Compose it at the TOP of a mode as <c>Selector(PanelGuard, ModeTree)</c>: while it returns
/// Running/Failure the mode tree doesn't run, so the bot won't act into a covered game.</para>
///
/// <para>v1 close = Escape only (PoE's universal "close top panel / step out of menu"). No
/// close-button geometry, so it's resolution- and layout-independent. Intentionally-open farming
/// panels (Inventory/Stash/Atlas during a deposit/insert) belong in the mechanic's expected set
/// so the guard leaves them alone.</para>
///
/// <para><b>Staged:</b> compiles and is ready; not yet inserted into the validated modes' trees
/// (that changes their control flow and needs a live pass). Wire + validate next round.</para>
/// </summary>
public sealed class PanelGuard : IBehavior
{
    private const double PressCooldownMs = 350; // let a panel animate closed before re-pressing
    private const int VK_ESCAPE = 0x1B;

    private readonly Func<BehaviorContext, IReadOnlySet<string>> _expectedSelector;
    private readonly int _maxPresses;
    private int _presses;
    private TimeSpan _lastPressAt = TimeSpan.MinValue;

    public string Name { get; }
    public BehaviorStatus LastStatus { get; private set; } = BehaviorStatus.Success;
    public string LastDecision { get; private set; } = "clean";

    /// <param name="expectedSelector">Panels the current mechanic legitimately has open this tick.</param>
    /// <param name="maxPresses">Escapes to try before giving up (panel Escape can't close).</param>
    public PanelGuard(string name, Func<BehaviorContext, IReadOnlySet<string>> expectedSelector, int maxPresses = 4)
    {
        Name = name;
        _expectedSelector = expectedSelector;
        _maxPresses = maxPresses;
    }

    /// <summary>Convenience: guard against the modal blocking set only (ignore intentional panels).</summary>
    public static PanelGuard BlockingOnly(string name = "panel guard", int maxPresses = 4)
        => new(name, _ => EmptySet, maxPresses);

    private static readonly IReadOnlySet<string> EmptySet = new HashSet<string>();

    public BehaviorStatus Tick(BehaviorContext ctx)
    {
        var expected = _expectedSelector(ctx);
        var extra = ctx.Snapshot.OpenPanels.OpenExcept(expected);
        if (extra.Count == 0)
        {
            _presses = 0;
            LastDecision = "clean";
            return LastStatus = BehaviorStatus.Success;
        }

        if (_presses >= _maxPresses)
        {
            LastDecision = $"stuck: [{string.Join(",", extra)}] survived {_presses} escapes";
            return LastStatus = BehaviorStatus.Failure;
        }

        var now = BotMonotonicClock.Now;
        if (BotMonotonicClock.ElapsedSince(_lastPressAt).TotalMilliseconds < PressCooldownMs)
        {
            LastDecision = $"waiting for [{string.Join(",", extra)}] to close";
            return LastStatus = BehaviorStatus.Running;
        }

        ctx.Input.TapKey(VK_ESCAPE, ClickIntent.InteractUi, "panel-guard escape");
        _presses++;
        _lastPressAt = now;
        LastDecision = $"escape #{_presses} to close [{string.Join(",", extra)}]";
        return LastStatus = BehaviorStatus.Running;
    }

    public void Reset()
    {
        _presses = 0;
        _lastPressAt = TimeSpan.MinValue;
        LastStatus = BehaviorStatus.Success;
    }
}
