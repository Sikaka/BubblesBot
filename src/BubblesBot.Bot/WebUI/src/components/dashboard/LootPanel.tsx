import { useStatusStore } from "../../state/statusStore";

export function LootPanel() {
  const ledger = useStatusStore((s) => s.status?.lootLedger);
  if (!ledger) return <div className="tree-empty">(no loot data)</div>;

  const categories = Object.entries(ledger.byCategory ?? {}).sort((a, b) => b[1] - a[1]);
  return (
    <>
      <div className="status-grid">
        <div className="status-row"><span className="k">Pickups</span><span className="v">{ledger.pickups}</span></div>
        <div className="status-row">
          <span className="k">Value</span>
          <span className="v good">{ledger.totalChaos.toFixed(1)}c
            {ledger.maxPlausibleChaos > ledger.totalChaos ? ` (≤${ledger.maxPlausibleChaos.toFixed(0)}c)` : ""}</span>
        </div>
        <div className="status-row"><span className="k">Rate</span><span className="v">{ledger.chaosPerHour.toFixed(1)} c/h</span></div>
        {categories.slice(0, 5).map(([category, chaos]) => (
          <div className="status-row" key={category}>
            <span className="k">{category}</span>
            <span className="v">{chaos.toFixed(1)}c</span>
          </div>
        ))}
      </div>
      {ledger.recent.length > 0 && (
        <div className="events loot-recent">
          {ledger.recent.slice(0, 15).map((entry, i) => (
            <div className="ev-row" key={i}>
              <span className="ev-t">{entry.at.slice(11, 19)}</span>{" "}
              <span className="ev-cat">[{entry.category}]</span>{" "}
              <span className="ev-msg">
                {entry.stackCount > 1 ? `${entry.stackCount}× ` : ""}{entry.name} — {entry.chaosValue.toFixed(1)}c
              </span>
            </div>
          ))}
        </div>
      )}
    </>
  );
}
