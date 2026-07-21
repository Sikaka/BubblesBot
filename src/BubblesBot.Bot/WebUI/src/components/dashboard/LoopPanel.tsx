import { useStatusStore } from "../../state/statusStore";

/** Map-run lifecycle telemetry — only rendered while mode 4 publishes a loop block. */
export function LoopPanel() {
  const status = useStatusStore((state) => state.status);
  const loop = status?.loop;
  if (status?.activeMode !== 4 || !loop) return null;
  return (
    <section className="card">
      <h2>Atlas loop</h2>
      <div className="status-grid">
        <div className="status-row"><span className="k">Step</span><span className="v">{loop.step}</span></div>
        <div className="status-row"><span className="k">Phase</span><span className="v">{String(loop.lifecyclePhase)}</span></div>
        <div className="status-row"><span className="k">Strategy</span><span className="v">{loop.preset}</span></div>
        <div className="status-row">
          <span className="k">Maps</span>
          <span className="v">{loop.mapsCompleted} / {loop.targetMaps}</span>
        </div>
        <div className="status-row"><span className="k">Items stashed</span><span className="v">{loop.itemsStashed}</span></div>
        <div className="status-row"><span className="k">Portal scrolls</span><span className="v">{loop.portalScrolls}</span></div>
        {loop.stopped && (
          <div className="status-row">
            <span className="k">Stopped</span>
            <span className="v bad">{loop.stopReason || "yes"}</span>
          </div>
        )}
      </div>
    </section>
  );
}
