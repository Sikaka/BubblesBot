namespace BubblesBot.Research.Probing;

/// <summary>
/// One self-contained reverse-engineering task. A probe knows how to (a) VALIDATE that a
/// committed <c>KnownOffsets</c> value still reads the right thing against the live game, and
/// (b) DISCOVER the new offset when validation fails.
///
/// <para>Probes are tiny and live one-per-file under <c>Probes/</c>. They are found by reflection
/// (see <see cref="ProbeRegistry"/>) — drop a new file implementing this interface and it is
/// picked up automatically, no central registration. Keep each probe small enough to paste into
/// an LLM context whole.</para>
///
/// <para>Truth comes from the gitignored baseline (your authority) first, with the optional
/// ExileAPI oracle as an accelerator + cross-check — never a hard dependency. See <see cref="Check"/>.</para>
/// </summary>
public interface IProbe
{
    /// <summary>Stable selector used on the CLI, e.g. <c>player.life</c>.</summary>
    string Name { get; }

    /// <summary>Sweep grouping, e.g. <c>chain</c>, <c>camera</c>, <c>serverdata</c>.</summary>
    string Group { get; }

    /// <summary>One-line human description.</summary>
    string Description { get; }

    /// <summary>
    /// Baseline fact keys this probe asserts against. If all are absent AND no oracle is present,
    /// the runner reports Skip instead of running blind.
    /// </summary>
    IReadOnlyList<string> RequiredFacts { get; }

    /// <summary>Fast path: read at the committed offset, compare to baseline/oracle.</summary>
    ProbeResult Validate(ProbeContext ctx);

    /// <summary>Slow path: hunt the (new) offset via value-scan / oracle address derivation.</summary>
    ProbeResult Discover(ProbeContext ctx);
}
