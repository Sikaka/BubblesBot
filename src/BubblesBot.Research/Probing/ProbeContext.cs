using BubblesBot.Core;
using BubblesBot.Research.Probing.Oracle;
using BubblesBot.Research.Probing.Toolkit;

namespace BubblesBot.Research.Probing;

/// <summary>
/// Everything a probe needs, resolved once per run and shared across the sweep:
///   - a live <see cref="MemoryReader"/>,
///   - the resolved pointer <see cref="Chain"/> (via AOB or value-scan; no ExileAPI required),
///   - the gitignored <see cref="Facts"/> baseline (the independent authority),
///   - an optional <see cref="Oracle"/> (ExileAPI/POEMCP) used only to accelerate + cross-check.
/// </summary>
public sealed class ProbeContext
{
    public required MemoryReader Reader { get; init; }
    public required ResolvedChain Chain { get; init; }
    public required Baseline Facts { get; init; }

    /// <summary>Never null — a <see cref="NullOracle"/> stands in when ExileAPI is absent.</summary>
    public required IGameOracle Oracle { get; init; }
    public string GameBuild { get; init; } = "unknown";
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Scratch for sharing derived anchors between probes within one run.</summary>
    public Dictionary<string, object> State { get; } = new();
}
