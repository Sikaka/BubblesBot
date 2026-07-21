using BubblesBot.Core.Snapshot;
using BubblesBot.Bot.Input;

namespace BubblesBot.Bot.Modes;

/// <summary>
/// One unit of bot behavior. The app drives exactly one mode at a time and gives it a fresh
/// snapshot every tick. Modes are passive observers of state and active issuers of input —
/// they never read memory directly and they never call <see cref="SendInputNative"/>; all
/// input goes through <see cref="IInputRouter"/>.
/// </summary>
public interface IBotMode
{
    string Name { get; }

    /// <summary>Per-frame entry point. Snapshot is one-tick-only; do not stash it.</summary>
    void Tick(GameSnapshot snapshot, IInputRouter input);

    /// <summary>Called when the mode becomes active or the area changes.</summary>
    void Reset() { }
}
