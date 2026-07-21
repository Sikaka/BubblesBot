using System.Globalization;
using BubblesBot.Core;
using BubblesBot.Core.Game;

namespace BubblesBot.Research.Validation;

/// <summary>
/// Tier-1 offset validation pipeline. Each <see cref="Probe"/> declares:
///   • Where the parent struct lives (<see cref="Probe.BaseAddressKey"/>),
///   • What our codebase claims the offset is (<see cref="Probe.OurOffset"/>),
///   • What POEMCP eval expression yields the ground truth value at that offset
///     (<see cref="Probe.TruthExpression"/>),
///   • What kind of value it is (<see cref="ProbeKind"/>) so we can compare correctly
///     and rescan for the right offset on mismatch.
///
/// <para>Run via <c>BubblesBot.Research --sweep-offsets</c>. For each probe the runner
/// reads the value through our offset, asks POEMCP for truth, compares. On mismatch it
/// scans a window around the assumed offset for the truth value and reports the candidate
/// — that's the proposed fix the human or future automation pastes back into
/// <c>KnownOffsets.cs</c>.</para>
///
/// <para>This file IS the offset playbook. Add a probe for every field we depend on; per
/// patch, run the sweep, fix what drifted. Probes for already-validated offsets stay as
/// regression tests — they're cheap and they catch drift before features fail.</para>
/// </summary>
public enum ProbeKind
{
    /// <summary>64-bit pointer. Truth is a hex address ("X") string.</summary>
    Pointer,
    /// <summary>32-bit signed int.</summary>
    Int32,
    /// <summary>32-bit IEEE float (compared with epsilon).</summary>
    Float,
    /// <summary>Single byte.</summary>
    Byte,
    /// <summary>16-bit unsigned int.</summary>
    UInt16,
}

/// <summary>One offset to validate. Pure data; behavior in <see cref="OffsetSweep.Run"/>.</summary>
public readonly record struct Probe(
    string Category,
    string FieldName,
    string BaseAddressKey,    // key into the cached BaseAddress dictionary
    int OurOffset,
    ProbeKind Kind,
    string TruthExpression);

public sealed record SweepResult(
    string Category,
    string Field,
    int OurOffset,
    string OursValue,
    string TruthValue,
    bool Match,
    string? RescanProposal,
    string? Error);

/// <summary>
/// Runs <see cref="Probe"/>s against live memory + POEMCP. Caches base addresses (one
/// POEMCP roundtrip per struct, not per field) and value-scans the parent struct on a
/// mismatch to propose the corrected offset.
/// </summary>
public sealed class OffsetSweep
{
    private readonly MemoryReader _reader;
    private readonly PoemcpClient _poemcp;
    private readonly Dictionary<string, nint> _bases = new();

    /// <summary>Half-window in bytes for the value-scan rescan on mismatch.</summary>
    public int RescanRadius { get; set; } = 0x300;

    public OffsetSweep(MemoryReader reader, PoemcpClient poemcp)
    {
        _reader = reader;
        _poemcp = poemcp;
    }

    /// <summary>
    /// Resolve base address for a struct via a POEMCP eval. Cached for the sweep run.
    /// Pass canonical short names like "ingameData" / "ingameState" / "serverData" / "camera"
    /// / "ingameUi" — see <see cref="OffsetProbeCatalog.SeedBaseExpressions"/>.
    /// </summary>
    public async Task<bool> ResolveBaseAsync(string key, string addressExpression, CancellationToken ct = default)
    {
        var r = await _poemcp.EvalAsync(addressExpression, ct);
        if (!r.Success) return false;
        try { _bases[key] = r.AsAddress(); return _bases[key] != 0; }
        catch { return false; }
    }

    public nint GetBase(string key) => _bases.TryGetValue(key, out var v) ? v : 0;

    /// <summary>Run a single probe. Returns the comparison result with a rescan proposal on mismatch.</summary>
    public async Task<SweepResult> RunAsync(Probe p, CancellationToken ct = default)
    {
        try
        {
            var basePtr = GetBase(p.BaseAddressKey);
            if (basePtr == 0) return new(p.Category, p.FieldName, p.OurOffset, "—", "—", false, null, $"base '{p.BaseAddressKey}' unresolved");

            var truthEval = await _poemcp.EvalAsync(p.TruthExpression, ct);
            if (!truthEval.Success) return new(p.Category, p.FieldName, p.OurOffset, "—", "—", false, null, $"POEMCP error: {truthEval.Error}");

            var (oursStr, truthStr, match) = Compare(basePtr, p, truthEval);
            string? rescan = null;
            if (!match) rescan = ProposeRescan(basePtr, p, truthEval);
            return new(p.Category, p.FieldName, p.OurOffset, oursStr, truthStr, match, rescan, null);
        }
        catch (Exception ex)
        {
            return new(p.Category, p.FieldName, p.OurOffset, "—", "—", false, null, ex.Message);
        }
    }

    private (string ours, string truth, bool match) Compare(nint basePtr, Probe p, EvalResult truth)
    {
        switch (p.Kind)
        {
            case ProbeKind.Pointer:
            {
                _reader.TryReadStruct<nint>(basePtr + p.OurOffset, out var ours);
                var t = truth.AsAddress();
                return ($"0x{(long)ours:X}", $"0x{(long)t:X}", ours == t);
            }
            case ProbeKind.Int32:
            {
                _reader.TryReadStruct<int>(basePtr + p.OurOffset, out var ours);
                var t = truth.AsInt();
                return (ours.ToString(), t.ToString(), ours == t);
            }
            case ProbeKind.Float:
            {
                _reader.TryReadStruct<float>(basePtr + p.OurOffset, out var ours);
                var t = truth.AsFloat();
                return (ours.ToString("F3", CultureInfo.InvariantCulture), t.ToString("F3", CultureInfo.InvariantCulture), Math.Abs(ours - t) < 1e-3f);
            }
            case ProbeKind.Byte:
            {
                _reader.TryReadStruct<byte>(basePtr + p.OurOffset, out var ours);
                var t = (byte)truth.AsInt();
                return (ours.ToString(), t.ToString(), ours == t);
            }
            case ProbeKind.UInt16:
            {
                _reader.TryReadStruct<ushort>(basePtr + p.OurOffset, out var ours);
                var t = (ushort)truth.AsInt();
                return (ours.ToString(), t.ToString(), ours == t);
            }
        }
        return ("?", "?", false);
    }

    /// <summary>
    /// Sweep a window around the claimed offset for the truth value. If found at a
    /// different offset, return that offset as the proposal. The window is large enough to
    /// cover most "compiler reordered fields" cases but bounded to avoid false hits in dense
    /// structs.
    /// </summary>
    private string? ProposeRescan(nint basePtr, Probe p, EvalResult truth)
    {
        var radius = RescanRadius;
        var alignment = p.Kind switch { ProbeKind.Pointer => 8, ProbeKind.Int32 or ProbeKind.Float => 4, ProbeKind.UInt16 => 2, _ => 1 };
        var matches = new List<int>();
        var startOff = Math.Max(0, p.OurOffset - radius);
        for (var off = startOff; off <= p.OurOffset + radius; off += alignment)
        {
            if (MatchAt(basePtr, off, p.Kind, truth)) matches.Add(off);
            if (matches.Count > 8) break;
        }
        if (matches.Count == 0) return null;
        return "candidates: " + string.Join(", ", matches.Select(o => $"0x{o:X}"));
    }

    private bool MatchAt(nint basePtr, int offset, ProbeKind kind, EvalResult truth)
    {
        try
        {
            switch (kind)
            {
                case ProbeKind.Pointer:
                    if (!_reader.TryReadStruct<nint>(basePtr + offset, out var p)) return false;
                    return p == truth.AsAddress() && p != 0;
                case ProbeKind.Int32:
                    if (!_reader.TryReadStruct<int>(basePtr + offset, out var i)) return false;
                    return i == truth.AsInt();
                case ProbeKind.Float:
                    if (!_reader.TryReadStruct<float>(basePtr + offset, out var f)) return false;
                    return Math.Abs(f - truth.AsFloat()) < 1e-3f;
                case ProbeKind.Byte:
                    if (!_reader.TryReadStruct<byte>(basePtr + offset, out var b)) return false;
                    return b == (byte)truth.AsInt();
                case ProbeKind.UInt16:
                    if (!_reader.TryReadStruct<ushort>(basePtr + offset, out var u)) return false;
                    return u == (ushort)truth.AsInt();
            }
        }
        catch { return false; }
        return false;
    }
}
