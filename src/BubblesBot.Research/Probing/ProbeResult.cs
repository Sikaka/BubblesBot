namespace BubblesBot.Research.Probing;

/// <summary>Outcome of a probe run.</summary>
public enum ProbeStatus
{
    /// <summary>OURS matched the authority (baseline, or oracle when no baseline).</summary>
    Pass,
    /// <summary>OURS did not match — the committed offset has likely drifted.</summary>
    Fail,
    /// <summary>No authority available (no baseline fact and no oracle) — nothing to check against.</summary>
    Skip,
    /// <summary>OURS matched the baseline, but the oracle disagrees with the baseline — baseline is stale.</summary>
    Conflict,
    /// <summary>A discovery run produced candidate offsets (not a pass/fail verdict).</summary>
    Candidates,
}

/// <summary>A single candidate offset surfaced by a probe's discovery path.</summary>
public sealed record OffsetCandidate(int Offset, string Note)
{
    public override string ToString() => $"+0x{Offset:X} {Note}".TrimEnd();
}

/// <summary>
/// The result of <see cref="IProbe.Validate"/> or <see cref="IProbe.Discover"/>.
/// Validate returns Pass/Fail/Skip/Conflict; Discover returns Candidates.
/// </summary>
public sealed record ProbeResult(
    ProbeStatus Status,
    string Message,
    IReadOnlyList<OffsetCandidate> Candidates)
{
    public static ProbeResult Pass(string message) => new(ProbeStatus.Pass, message, []);
    public static ProbeResult Fail(string message) => new(ProbeStatus.Fail, message, []);
    public static ProbeResult Skip(string message) => new(ProbeStatus.Skip, message, []);
    public static ProbeResult Conflict(string message) => new(ProbeStatus.Conflict, message, []);

    /// <summary>Discovery produced a ranked list of candidate offsets (relative to the struct base).</summary>
    public static ProbeResult Found(string field, IEnumerable<OffsetCandidate> candidates)
    {
        var list = candidates.ToList();
        return new(ProbeStatus.Candidates,
            list.Count == 0 ? $"{field}: no candidates found" : $"{field}: {list.Count} candidate(s)",
            list);
    }

    /// <summary>Discovery derived a single exact offset (e.g. from an oracle-supplied address).</summary>
    public static ProbeResult Exact(string field, int offset, string note = "exact") =>
        new(ProbeStatus.Candidates, $"{field} -> +0x{offset:X}", [new OffsetCandidate(offset, note)]);

    /// <summary>
    /// Merge several field verdicts into one for multi-field probes. Severity order:
    /// Fail &gt; Conflict &gt; Pass &gt; Skip (any Fail makes the whole probe Fail, etc.).
    /// </summary>
    public static ProbeResult Combine(params ProbeResult[] parts)
    {
        static int Rank(ProbeStatus s) => s switch
        {
            ProbeStatus.Fail => 4,
            ProbeStatus.Conflict => 3,
            ProbeStatus.Pass => 2,
            ProbeStatus.Skip => 1,
            _ => 0,
        };
        var status = parts.OrderByDescending(p => Rank(p.Status)).First().Status;
        return new(status, string.Join("; ", parts.Select(p => p.Message)),
            parts.SelectMany(p => p.Candidates).ToList());
    }
}
