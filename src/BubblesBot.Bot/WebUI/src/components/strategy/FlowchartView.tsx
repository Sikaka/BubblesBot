import type { FarmingStrategy, MechanicType } from "../../api/strategy";
import { MECHANIC_LABELS } from "../../api/strategy";
import { useStatusStore } from "../../state/statusStore";

export type FlowEditTarget =
  | { kind: "supplies" }
  | { kind: "mechanic"; mechanic: MechanicType }
  | { kind: "completion" }
  | { kind: "limits" };

interface Props {
  strategy: FarmingStrategy;
  onEdit?: (target: FlowEditTarget) => void;
}

interface FlowNodeProps {
  label: string;
  detail: string;
  phases: string[];
  lifecyclePhase: string | null;
  target?: FlowEditTarget;
  onEdit?: (target: FlowEditTarget) => void;
  badge?: string;
  off?: boolean;
}

function FlowNode({ label, detail, phases, lifecyclePhase, target, onEdit, badge, off }: FlowNodeProps) {
  const active = lifecyclePhase != null && phases.includes(lifecyclePhase);
  const editable = !!target && !!onEdit;
  return (
    <button
      type="button"
      className={`logic-node ${active ? "active" : ""} ${off ? "off" : ""} ${editable ? "clickable" : ""}`}
      disabled={!editable}
      onClick={() => target && onEdit?.(target)}
    >
      <span className="logic-node-topline">
        <strong>{label}</strong>
        {active && <span className="logic-badge live">Live</span>}
        {badge && <span className="logic-badge">{badge}</span>}
      </span>
      <span className="logic-node-detail">{detail}</span>
      {target && <span className="logic-edit">Edit policy</span>}
    </button>
  );
}

function Arrow({ label }: { label?: string }) {
  return <div className="logic-arrow"><span>{label}</span><b>-&gt;</b></div>;
}

export function FlowchartView({ strategy, onEdit }: Props) {
  const lifecyclePhase = useStatusStore((state) => {
    const loop = state.status?.loop;
    return loop && state.status?.activeMode === 4 ? String(loop.lifecyclePhase) : null;
  });
  const enabledMechanics = strategy.mechanics.filter((block) => block.enabled);

  return (
    <div className="logic-canvas">
      <div className="logic-intro">
        <div>
          <span className="setup-kicker">Runtime map</span>
          <h3>What the bot will do, in order</h3>
          <p>Click any configurable node to change its policy. The connections mirror the fixed, tested runtime lifecycle.</p>
        </div>
        <div className="logic-summary">
          <div><span>Map</span><strong>{strategy.supply.map.targetMapName || "Any configured map"}</strong></div>
          <div><span>Run target</span><strong>{strategy.completion.targetMaps}</strong></div>
          <div><span>Mechanics</span><strong>{enabledMechanics.length}</strong></div>
        </div>
      </div>

      <section className="logic-stage">
        <div className="logic-stage-label"><span>01</span><div><strong>Prepare in hideout</strong><small>Runs before every map</small></div></div>
        <div className="logic-track">
          <FlowNode label="Deposit loot" detail={strategy.loot.depositAfterEachMap ? "Empty inventory into dump tab" : "Keep carried loot"}
            phases={["Deposit"]} lifecyclePhase={lifecyclePhase} target={{ kind: "supplies" }} onEdit={onEdit}
            badge={strategy.loot.depositAfterEachMap ? "On" : "Off"} />
          <Arrow />
          <FlowNode label="Withdraw recipe" detail={`${strategy.supply.map.targetMapName || "Map"} + ${strategy.supply.scarabs.reduce((sum, line) => sum + line.countPerMap, 0)} scarabs`}
            phases={["Preparation"]} lifecyclePhase={lifecyclePhase} target={{ kind: "supplies" }} onEdit={onEdit} />
          <Arrow />
          <FlowNode label="Configure atlas" detail={strategy.mapPrep.atlasNodeName || "No atlas node selected"}
            phases={["Preparation", "Device"]} lifecyclePhase={lifecyclePhase} target={{ kind: "supplies" }} onEdit={onEdit} />
          <Arrow />
          <FlowNode label="Open & enter" detail="Stage map, activate device, enter portal"
            phases={["Device", "Entry"]} lifecyclePhase={lifecyclePhase} target={{ kind: "supplies" }} onEdit={onEdit} />
        </div>
      </section>

      <div className="logic-drop"><span>portal transition</span><b>v</b></div>

      <section className="logic-stage in-map">
        <div className="logic-stage-label"><span>02</span><div><strong>Clear the map</strong><small>Exploration and combat stay on the tested main path</small></div></div>
        <div className="logic-track main-track">
          <FlowNode label="Explore & engage" detail={`Sweep until ${strategy.completion.explorationDonePercent}% explored`}
            phases={["Clear"]} lifecyclePhase={lifecyclePhase} target={{ kind: "completion" }} onEdit={onEdit} />
          <Arrow label="when found" />
          <div className="logic-branch">
            <div className="logic-branch-head"><strong>Mechanic policies</strong><span>Evaluated during the sweep</span></div>
            <div className="logic-mechanics-grid">
              {strategy.mechanics.map((block) => (
                <FlowNode
                  key={block.type}
                  label={MECHANIC_LABELS[block.type]}
                  detail={block.enabled ? mechanicDetail(strategy, block.type) : "Skipped when discovered"}
                  phases={["Clear", "BossMechanics"]}
                  lifecyclePhase={lifecyclePhase}
                  target={{ kind: "mechanic", mechanic: block.type }}
                  onEdit={onEdit}
                  badge={block.enabled ? "On" : "Off"}
                  off={!block.enabled}
                />
              ))}
            </div>
          </div>
          <Arrow label="resume sweep" />
          <FlowNode label="Completion gate" detail="All enabled requirements must pass"
            phases={["Completion", "BossMechanics"]} lifecyclePhase={lifecyclePhase} target={{ kind: "completion" }} onEdit={onEdit}
            badge={`${strategy.completion.explorationDonePercent}%`} />
          <Arrow />
          <FlowNode label="Final loot & exit" detail="Portal out, record run, begin the next map"
            phases={["Exit", "Report"]} lifecyclePhase={lifecyclePhase} target={{ kind: "limits" }} onEdit={onEdit} />
        </div>
        <div className="logic-conditions">
          <span className="logic-condition active">Exploration at least {strategy.completion.explorationDonePercent}%</span>
          <span className={`logic-condition ${strategy.completion.requireBossKill ? "active" : "muted"}`}>Boss kill {strategy.completion.requireBossKill ? "required" : "optional"}</span>
          <span className="logic-condition active">Mechanic stalls at most {strategy.limits.maxMechanicStallsPerMap}</span>
          <span className="logic-condition active">Zone limit {strategy.limits.maxZoneMinutes ?? "profile default"} min</span>
        </div>
      </section>

      <div className="logic-loop">
        <span>Repeat until</span>
        <strong>{strategy.completion.targetMaps} maps complete</strong>
        <button type="button" onClick={() => onEdit?.({ kind: "completion" })}>Change target</button>
      </div>
    </div>
  );
}

function mechanicDetail(strategy: FarmingStrategy, type: MechanicType): string {
  const block = strategy.mechanics.find((candidate) => candidate.type === type);
  if (!block) return "Not configured";
  if (block.type === "eldritchAltars") return `${block.policy} choice policy`;
  if (block.type === "ritual") return block.deferUntilMapSweep ? "Run after map sweep" : "Run when discovered";
  if (block.type === "delirium") return `${block.maximumPackDwellSeconds}s maximum pack dwell`;
  if (block.type === "ultimatum") return block.exitAfter ? "Run, then exit map" : "Run, then resume map";
  return block.sweepBias === 0 ? "Run when discovered" : `Sweep priority ${block.sweepBias > 0 ? "+" : ""}${block.sweepBias}`;
}
