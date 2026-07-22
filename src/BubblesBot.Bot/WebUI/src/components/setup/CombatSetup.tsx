import type { Schema, Settings } from "../../api/types";
import { fieldPath, pathGet } from "../../lib/paths";
import { SchemaForm } from "../schema/SchemaForm";

interface CombatPreset {
  id: string;
  name: string;
  eyebrow: string;
  description: string;
  support: "ready" | "foundation";
  behavior: string[];
  values: Record<string, unknown>;
}

const PRESETS: CombatPreset[] = [
  {
    id: "triggered-tank",
    name: "Triggered tank",
    eyebrow: "Cast when stunned / RF / aura",
    description: "Commit to the biggest dense pack and hold inside it while automatic damage does the work.",
    support: "ready",
    behavior: ["Find a dense pack", "Move into the pack", "Hold position", "Confirm damage or move on"],
    values: {
      mapClearStance: 1,
      proximityHoldRadiusGrid: 8,
      proximityEngageRadiusGrid: 80,
      proximityDestinationPolicy: 1,
      proximityDensityRadiusGrid: 30,
      minPackDetourSize: 4,
    },
  },
  {
    id: "minion-escort",
    name: "Minion escort",
    eyebrow: "Chains of Command / passive summons",
    description: "Approach dense packs, stop at their edge, and let summons engage without walking into the middle.",
    support: "ready",
    behavior: ["Find a dense pack", "Approach its edge", "Hold nearby", "Move when the pack is resolved"],
    values: {
      mapClearStance: 1,
      proximityHoldRadiusGrid: 28,
      proximityEngageRadiusGrid: 80,
      proximityDestinationPolicy: 1,
      proximityDensityRadiusGrid: 25,
      minPackDetourSize: 4,
    },
  },
  {
    id: "ranged",
    name: "Ranged attacker",
    eyebrow: "Bow / spell caster",
    description: "Stay near packs and cast toward enemies. The current engine uses drive-by attacks; continuous orbiting is phase 2.",
    support: "ready",
    behavior: ["Find a nearby pack", "Dash or close to line-of-fire", "Stop at standoff range", "Hold attack and retarget"],
    values: {
      mapClearStance: 2,
      combatEngageRange: 85,
      rangedStandoffGrid: 45,
      rangedUseDashToClose: true,
      minPackDetourSize: 4,
    },
  },
  {
    id: "melee",
    name: "Melee attacker",
    eyebrow: "Strike / slam / close-range channel",
    description: "Close to attack range and work through enemies. Pack-edge traversal and directional chaining are phase 2.",
    support: "foundation",
    behavior: ["Find a nearby pack", "Close to melee range", "Aim and attack", "Advance through the map"],
    values: {
      mapClearStance: 0,
      combatEngageRange: 25,
      minPackDetourSize: 3,
    },
  },
];

interface Props {
  schema: Schema;
  values: Settings;
  saved: Settings;
  onChange: (path: string[], value: unknown) => void;
  onApply: (values: Record<string, unknown>) => void;
}

export function CombatSetup({ schema, values, saved, onChange, onApply }: Props) {
  const setting = (name: string) => {
    const field = schema.fields.find((candidate) => candidate.name === name);
    return field ? pathGet(values, fieldPath(field)) : undefined;
  };
  const stance = Number(setting("mapClearStance") ?? 0);
  const holdRadius = Number(setting("proximityHoldRadiusGrid") ?? 14);

  const selectedId = stance === 1
    ? (holdRadius >= 20 ? "minion-escort" : "triggered-tank")
    : stance === 2 ? "ranged"
    : Number(setting("combatEngageRange") ?? 50) <= 35 ? "melee" : "ranged";
  const selected = PRESETS.find((preset) => preset.id === selectedId) ?? PRESETS[0];

  return (
    <div className="combat-setup">
      <div className="setup-lead">
        <span className="setup-kicker">Start with intent</span>
        <h3>How should this character approach a pack?</h3>
        <p>Choose the closest behavior. It applies safe defaults using settings the combat engine already supports; you can tune every value below.</p>
      </div>

      <div className="combat-preset-grid">
        {PRESETS.map((preset) => (
          <button
            key={preset.id}
            type="button"
            className={`combat-preset ${selectedId === preset.id ? "chosen" : ""}`}
            onClick={() => onApply(preset.values)}
          >
            <span className="combat-preset-topline">
              <span className="combat-preset-name">{preset.name}</span>
              <span className={`capability-badge ${preset.support}`}>{preset.support === "ready" ? "Supported" : "Phase 1"}</span>
            </span>
            <span className="combat-preset-eyebrow">{preset.eyebrow}</span>
            <span className="combat-preset-description">{preset.description}</span>
          </button>
        ))}
      </div>

      <div className="behavior-preview" aria-label={`${selected.name} behavior preview`}>
        <div className="behavior-preview-head">
          <div>
            <span className="setup-kicker">Behavior preview</span>
            <strong>{selected.name}</strong>
          </div>
          <span className="behavior-live-note">This is what the bot can execute today</span>
        </div>
        <div className="behavior-steps">
          {selected.behavior.map((label, index) => (
            <span className="behavior-step-wrap" key={label}>
              <span className="behavior-step"><b>{index + 1}</b>{label}</span>
              {index < selected.behavior.length - 1 && <span className="behavior-arrow">-&gt;</span>}
            </span>
          ))}
        </div>
      </div>

      {selected.support === "foundation" && (
        <div className="phase-note">
          <strong>Phase 2 boundary:</strong> orbit direction, kite distance, threat-weighted aim, and swing-by-swing traversal need new runtime behavior. These controls intentionally do not pretend to configure them yet.
        </div>
      )}

      <details className="advanced-settings">
        <summary>Fine-tune combat distances and safety</summary>
        <SchemaForm fields={schema.fields} values={values} saved={saved} onChange={onChange} categories={["Combat"]} />
      </details>
    </div>
  );
}
