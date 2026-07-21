# Probe suite — oracle-free RE apparatus

A modular reverse-engineering harness for validating and re-discovering PoE memory offsets, **without
depending on ExileAPI/POEMCP**. ExileAPI is supported as an *optional* accelerator + cross-check, but
the suite's authority is your own gitignored baseline of ground-truth facts.

## Mental model

Three sources of truth per check:

- **OURS** — value read at the committed `KnownOffsets` value (what the bot will use).
- **BASE** — a fact in `baseline.local.json` (gitignored). Your independent authority.
- **ORACLE** — ExileAPI via POEMCP. Present only with `--poemcp` *and* when it's reachable.

Verdicts (see `Check.cs`):

| OURS | BASE | ORACLE | Verdict |
|---|---|---|---|
| =BASE | yes | absent | PASS |
| =BASE | yes | =BASE | PASS (corroborated) |
| =BASE | yes | ≠BASE | **CONFLICT** — baseline stale, re-capture |
| ≠BASE | yes | — | **FAIL** — offset drifted (auto-runs Discover) |
| — | no | =ORACLE | PASS (oracle-only; capture a baseline) |
| — | no | ≠ORACLE | FAIL |
| — | no | none | SKIP |

## CLI

```
dotnet run --project src/BubblesBot.Research -- <args>

--list                          list probes
--probe <name> [--discover]     validate one probe; --discover hunts the offset
--sweep [--group <g>]           validate all probes (or one group)
--baseline show|set|capture     manage gitignored facts
     set key=value ...          set facts explicitly (headless)
     capture                    interactive prompts
     capture --from-poemcp      auto-fill every fact from ExileAPI (one-shot, patch day)
--dump <hexAddr> [--dump-len N] hex/qword window dump for manual inspection

modifiers:
  --poemcp                      use ExileAPI as oracle (accelerate + cross-check)
  --hp <N> [--mana N] [--es-max N]   value-scan anchor when the AOB chain is stale
```

`--list` and `--baseline` work without the game running. Everything else attaches to PoE.

## Patch-day loop (no community tools required)

1. `--baseline capture --from-poemcp` if ExileAPI already updated — else `--baseline capture` (you type
   HP / area / league, read off your screen) or `--baseline set k=v`.
2. `--sweep` — PASS rows are fine; FAIL rows auto-print candidate offsets; CONFLICT means your baseline
   is stale.
3. Paste fixed offsets into `KnownOffsets.cs`, re-sweep until green.
4. If the root chain itself moved, `ChainResolver` falls back to `--hp` value-scan; re-AOB later.

## Writing a new probe

Drop one file under `Probes/<Area>/`. It's found by reflection — no registration. Keep it small.

```csharp
public sealed class MyProbe : IProbe
{
    public string Name => "area.thing";
    public string Group => "area";
    public string Description => "what it checks";
    public IReadOnlyList<string> RequiredFacts => ["area.thing"];

    public ProbeResult Validate(ProbeContext ctx)
    {
        var v = /* read at committed offset via ctx.Reader + KnownOffsets */;
        return Check.Int(ctx, "area.thing", v, "Struct.Field");
    }

    public ProbeResult Discover(ProbeContext ctx)
    {
        // value-scan / oracle-address subtraction via Toolkit.MemScan, return candidate offsets
        return ProbeResult.Found("Struct.Field", /* candidates */);
    }
}
```

Add the matching oracle expression(s) to `Oracle/OracleKeys.cs` so `--poemcp` can corroborate/accelerate.

## Layout

- `Probing/` — framework: `IProbe`, `ProbeContext`, `ProbeResult`, `Check`, `ProbeRegistry`,
  `ProbeRunner`, `Baseline`, `ProbeCli`.
- `Probing/Oracle/` — `IGameOracle` + `NullOracle` / `PoemcpOracle`, `OracleKeys`.
- `Probing/Toolkit/` — oracle-free primitives: `ChainResolver`, `ResolvedChain`, `MemScan`,
  `PageDiff`, `MemDump`.
- `Probes/` — the probes (one per file).

The legacy POEMCP-bound tests under `Validation/` still run via `--validate-all` / `--sweep-offsets`;
they will migrate into probes over time.
