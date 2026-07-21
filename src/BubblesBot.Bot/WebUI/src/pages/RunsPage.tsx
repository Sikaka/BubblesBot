import { useEffect, useState } from "react";
import { fetchRuns, type RunReport, type RunsSummary } from "../api/client";

const RESULT_CLASS: Record<string, string> = { completed: "good", died: "bad", stopped: "warn", disarmed: "" };

export default function RunsPage() {
  const [runs, setRuns] = useState<RunReport[]>([]);
  const [summary, setSummary] = useState<RunsSummary | null>(null);
  const [selected, setSelected] = useState<RunReport | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    fetchRuns(200)
      .then((r) => { setRuns(r.runs); setSummary(r.summary); })
      .catch((e) => setError(String(e)));
  }, []);

  return (
    <>
      <section className="card">
        <h2>Run history</h2>
        {error && <div className="v bad">{error}</div>}
        {summary && (
          <div className="status-grid">
            <div className="status-row"><span className="k">Runs recorded</span><span className="v">{summary.runs}</span></div>
            <div className="status-row"><span className="k">Maps completed</span><span className="v">{summary.mapsCompleted}</span></div>
            <div className="status-row"><span className="k">Total chaos</span><span className="v good">{summary.totalChaos.toFixed(0)}c</span></div>
            <div className="status-row"><span className="k">Chaos/hour</span><span className="v">{summary.chaosPerHour.toFixed(0)} c/h</span></div>
            <div className="status-row"><span className="k">Deaths</span><span className={`v ${summary.deaths > 0 ? "bad" : ""}`}>{summary.deaths}</span></div>
          </div>
        )}
        {runs.length > 0 && <LootSparkline runs={runs} />}
      </section>

      <section className="card">
        <h2>Runs <span className="card-hint">({runs.length})</span></h2>
        {runs.length === 0 ? (
          <div className="tree-empty">No runs recorded yet — reports are written as maps complete.</div>
        ) : (
          <div className="runs-table">
            <div className="runs-head">
              <span>Ended</span><span>Map</span><span>Result</span><span>Dur</span><span>Loot</span><span>c/h</span>
            </div>
            {runs.map((run) => (
              <button type="button" className="runs-row" key={run.runId} onClick={() => setSelected(run)}>
                <span>{run.endedUtc.slice(5, 19).replace("T", " ")}</span>
                <span className="runs-map">{run.mapName || run.strategyName}</span>
                <span className={`v ${RESULT_CLASS[run.result] ?? ""}`}>{run.result}</span>
                <span>{Math.round(run.durationSec / 60)}m</span>
                <span className="good">{run.lootChaos.toFixed(0)}c</span>
                <span>{run.chaosPerHour.toFixed(0)}</span>
              </button>
            ))}
          </div>
        )}
      </section>

      {selected && (
        <div className="drawer-backdrop" onClick={() => setSelected(null)}>
          <div className="drawer" onClick={(e) => e.stopPropagation()}>
            <div className="drawer-head">
              <h2>Run detail</h2>
              <button type="button" className="btn-secondary sm" onClick={() => setSelected(null)}>Close</button>
            </div>
            <div className="status-grid">
              <Detail k="Result" v={selected.result} cls={RESULT_CLASS[selected.result]} />
              <Detail k="Strategy" v={selected.strategyName} />
              <Detail k="Map" v={selected.mapName} />
              <Detail k="Character" v={selected.profile} />
              <Detail k="League" v={selected.league} />
              <Detail k="Map index" v={`${selected.mapIndex}`} />
              <Detail k="Started" v={selected.startedUtc.slice(0, 19).replace("T", " ")} />
              <Detail k="Duration" v={`${Math.round(selected.durationSec / 60)}m ${Math.round(selected.durationSec % 60)}s`} />
              <Detail k="Loot (map)" v={`${selected.lootChaos.toFixed(1)}c`} cls="good" />
              <Detail k="Items picked" v={`${selected.itemsPicked}`} />
              <Detail k="Chaos/hour" v={`${selected.chaosPerHour.toFixed(0)}`} />
              <Detail k="Deaths" v={`${selected.deaths}`} cls={selected.deaths > 0 ? "bad" : ""} />
            </div>
            {selected.stopReason && <div className="v warn run-stop-reason">Stop reason: {selected.stopReason}</div>}
          </div>
        </div>
      )}
    </>
  );
}

function Detail({ k, v, cls = "" }: { k: string; v: string; cls?: string }) {
  return <div className="status-row"><span className="k">{k}</span><span className={`v ${cls}`}>{v || "—"}</span></div>;
}

/** Hand-rolled SVG sparkline of per-run loot (most recent runs, left→right chronological). */
function LootSparkline({ runs }: { runs: RunReport[] }) {
  const series = [...runs].reverse().map((r) => r.lootChaos);
  if (series.length < 2) return null;
  const w = 600, h = 60, pad = 4;
  const max = Math.max(...series, 1);
  const points = series.map((v, i) => {
    const x = pad + (i / (series.length - 1)) * (w - 2 * pad);
    const y = h - pad - (v / max) * (h - 2 * pad);
    return `${x.toFixed(1)},${y.toFixed(1)}`;
  }).join(" ");
  return (
    <div className="sparkline-wrap">
      <div className="desc">Loot per run (chaos), oldest → newest</div>
      <svg className="sparkline" viewBox={`0 0 ${w} ${h}`} preserveAspectRatio="none">
        <polyline points={points} fill="none" stroke="var(--good)" strokeWidth="1.5" />
      </svg>
    </div>
  );
}
