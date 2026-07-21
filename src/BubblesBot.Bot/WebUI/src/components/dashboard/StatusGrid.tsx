import { useStatusStore } from "../../state/statusStore";
import { modeDefinition } from "../../lib/modes";

function Row({ k, v, cls = "" }: { k: string; v: string | number; cls?: string }) {
  return (
    <div className="status-row">
      <span className="k">{k}</span>
      <span className={`v ${cls}`}>{v}</span>
    </div>
  );
}

export function StatusGrid() {
  const s = useStatusStore((store) => store.status);
  if (!s) return <div className="tree-empty">(waiting for status…)</div>;

  const stateClass = !s.connected ? "bad" : !s.shouldAct ? "warn" : s.shouldLoot ? "good" : "";
  const timer = s.runTimer;
  const timerText = timer?.running
    ? `${Math.floor(timer.elapsedSeconds / 60)}m${timer.remainingSeconds != null ? ` (${Math.max(0, Math.floor(timer.remainingSeconds / 60))}m left)` : ""}`
    : "—";

  return (
    <div className="status-grid">
      <Row k="State" v={s.stateLabel} cls={stateClass} />
      <Row k="Foreground" v={s.foreground ? "yes" : "no"} cls={s.foreground ? "good" : "warn"} />
      <Row k="HP" v={`${s.playerHp ?? 0} / ${s.playerHpMax ?? 0}`} />
      <Row k="Grid" v={`(${s.playerGridX ?? 0}, ${s.playerGridY ?? 0})`} />
      <Row k="Area" v={`0x${(s.areaHash ?? 0).toString(16).toUpperCase()}`} />
      <Row k="Labels" v={s.labelsVisible ?? 0} />
      <Row k="Loot key" v={s.lootKeyHeld ? "HELD" : "released"} cls={s.lootKeyHeld ? "good" : ""} />
      <Row k="Mode" v={modeDefinition(s.activeMode).name} />
      <Row k="Decision" v={s.modeDecision || s.lootDecision || ""} />
      <Row k="Input" v={s.inputState ?? ""} />
      <Row k="Character" v={s.character || "—"} />
      <Row k="League" v={s.league || "—"} />
      <Row k="Run timer" v={timerText} cls={timer?.expired ? "bad" : ""} />
      <Row k="Run" v={s.runId || "—"} />
    </div>
  );
}
