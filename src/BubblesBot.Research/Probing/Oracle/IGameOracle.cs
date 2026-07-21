namespace BubblesBot.Research.Probing.Oracle;

/// <summary>
/// An optional, authoritative source of live game truth — in practice ExileAPI via POEMCP.
/// Probes treat it as an accelerator (it can hand back an exact value or even an exact address,
/// so discovery becomes a subtraction instead of a scan) and as a cross-check (Validate flags a
/// stale baseline when the oracle disagrees). It is never required: when ExileAPI isn't running
/// or hasn't been updated for the current patch, <see cref="NullOracle"/> stands in and every
/// call returns false, so the independent value-scan / baseline path takes over.
/// </summary>
public interface IGameOracle
{
    /// <summary>True when the oracle is reachable and answering.</summary>
    bool IsAvailable { get; }

    /// <summary>Truth VALUE for a fact key (e.g. "character.hp") as its string form.</summary>
    bool TryGetValue(string key, out string value);

    /// <summary>Truth ADDRESS for a struct/component key (e.g. "player.life") — enables offset = addr - base.</summary>
    bool TryGetAddress(string key, out nint addr);

    /// <summary>Escape hatch: evaluate an arbitrary ExileAPI expression for bespoke probes.</summary>
    bool TryEval(string expression, out string result);
}
