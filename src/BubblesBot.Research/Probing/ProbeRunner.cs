namespace BubblesBot.Research.Probing;

/// <summary>
/// Runs probes and prints results. On a Validate FAIL it automatically chains into Discover so the
/// candidate offsets are right there on patch day. Aggregates a sweep summary and returns a process
/// exit code (0 = all good, 2 = something failed/conflicted).
/// </summary>
public static class ProbeRunner
{
    public sealed record Tally(int Pass, int Fail, int Skip, int Conflict)
    {
        public int ExitCode => Fail > 0 || Conflict > 0 ? 2 : 0;
    }

    /// <summary>Run one probe (Validate, or Discover when <paramref name="discover"/> is set).</summary>
    public static ProbeStatus RunOne(IProbe probe, ProbeContext ctx, bool discover)
    {
        var artifact = ExecuteOne(probe, ctx, discover);
        ProbeArtifactWriter.Write(ctx, [artifact]);
        return artifact.Status;
    }

    private static ProbeExecutionArtifact ExecuteOne(IProbe probe, ProbeContext ctx, bool discover)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {probe.Name}  [{probe.Group}] ===");
        Console.WriteLine($"    {probe.Description}");

        if (discover)
        {
            var d = Safe(() => probe.Discover(ctx), "discover");
            PrintCandidates(d);
            return new ProbeExecutionArtifact(
                probe.Name, probe.Group, probe.Description, d.Status, d.Message, d.Candidates, d);
        }

        var r = Safe(() => probe.Validate(ctx), "validate");
        PrintVerdict(r);
        ProbeResult? discovery = null;
        if (r.Status == ProbeStatus.Fail)
        {
            Console.WriteLine("    -> drifted; running discovery:");
            discovery = Safe(() => probe.Discover(ctx), "discover");
            PrintCandidates(discovery);
        }
        return new ProbeExecutionArtifact(
            probe.Name, probe.Group, probe.Description, r.Status, r.Message, r.Candidates, discovery);
    }

    /// <summary>Run a set of probes in Validate mode and summarize.</summary>
    public static Tally Sweep(IEnumerable<IProbe> probes, ProbeContext ctx)
    {
        int pass = 0, fail = 0, skip = 0, conflict = 0;
        var artifacts = new List<ProbeExecutionArtifact>();
        foreach (var p in probes)
        {
            var artifact = ExecuteOne(p, ctx, discover: false);
            artifacts.Add(artifact);
            switch (artifact.Status)
            {
                case ProbeStatus.Pass: pass++; break;
                case ProbeStatus.Fail: fail++; break;
                case ProbeStatus.Skip: skip++; break;
                case ProbeStatus.Conflict: conflict++; break;
                case ProbeStatus.Candidates: break;
            }
        }
        var tally = new Tally(pass, fail, skip, conflict);
        Console.WriteLine();
        Console.WriteLine($"Summary: {pass} pass, {fail} fail, {conflict} conflict, {skip} skip");
        ProbeArtifactWriter.Write(ctx, artifacts);
        return tally;
    }

    private static void PrintVerdict(ProbeResult r)
    {
        var tag = r.Status switch
        {
            ProbeStatus.Pass => "PASS",
            ProbeStatus.Fail => "FAIL",
            ProbeStatus.Skip => "SKIP",
            ProbeStatus.Conflict => "CONFLICT",
            _ => "?",
        };
        Console.WriteLine($"    [{tag}] {r.Message}");
    }

    private static void PrintCandidates(ProbeResult r)
    {
        Console.WriteLine($"    {r.Message}");
        foreach (var c in r.Candidates)
            Console.WriteLine($"      {c}");
    }

    private static ProbeResult Safe(Func<ProbeResult> run, string phase)
    {
        try { return run(); }
        catch (Exception ex) { return ProbeResult.Fail($"{phase} threw {ex.GetType().Name}: {ex.Message}"); }
    }
}
