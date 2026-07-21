import { useState } from "react";
import type { LiveSkill, SkillSlot, SlotProfile } from "../../../api/types";
import { useStatusStore } from "../../../state/statusStore";
import { KeycodeButton } from "./KeycodeButton";

const ROLES = [
  { name: "Disabled", hint: "Keep the binding on file, but never use it." },
  { name: "Movement", hint: "Held while travelling. Configure exactly one movement skill." },
  { name: "Dash", hint: "Tapped for mobility and, optionally, crossing gaps." },
  { name: "Attack", hint: "Aimed at enemies during drive-by combat." },
  { name: "Self buff", hint: "Cast on the character to maintain a buff or warcry." },
  { name: "Channel", hint: "Held while its combat condition remains true." },
  { name: "Aura", hint: "Recorded as an aura; the current runtime does not toggle it automatically." },
  { name: "Mark / curse", hint: "Aimed at the highest-priority rare or unique target." },
];
const ROLE_DASH = 2;

// PoE's default skill-bar slot to key binding. Slots 8+ are modifier-bound extras.
const SLOT_DEFAULT_KEY: { vk: number; label: string }[] = [
  { vk: 0x01, label: "LMB" },
  { vk: 0x04, label: "MMB" },
  { vk: 0x02, label: "RMB" },
  { vk: 0x51, label: "Q" },
  { vk: 0x57, label: "W" },
  { vk: 0x45, label: "E" },
  { vk: 0x52, label: "R" },
  { vk: 0x54, label: "T" },
];

interface Props {
  value: SlotProfile<SkillSlot> | undefined;
  onChange: (value: SlotProfile<SkillSlot>) => void;
}

export function SkillsEditor({ value, onChange }: Props) {
  const profile: SlotProfile<SkillSlot> = value ?? { slots: [] };
  const slots = profile.slots ?? [];
  const [showDetected, setShowDetected] = useState(false);

  const setSlots = (next: SkillSlot[]) => onChange({ ...profile, slots: next });
  const updateSlot = (index: number, patch: Partial<SkillSlot>) =>
    setSlots(slots.map((slot, i) => (i === index ? { ...slot, ...patch } : slot)));

  const importDetected = (entry: LiveSkill) => {
    const def = SLOT_DEFAULT_KEY[entry.barSlot] ?? { vk: 0, label: "" };
    const name = entry.name?.length ? entry.name : def.label ? `${def.label} skill` : `Skill ${entry.barSlot}`;
    setSlots([
      ...slots,
      {
        name,
        vk: def.vk,
        role: 0,
        canCrossGaps: false,
        minCastIntervalMs: 100,
        maxRangeGrid: 30,
        chargeCount: Math.max(1, entry.maxUses || 1),
        chargeRechargeMs: 3000,
        gemId: entry.gemId,
      },
    ]);
  };

  const movementCount = slots.filter((slot) => Number(slot.role) === 1).length;
  const actionCount = slots.filter((slot) => [3, 5, 7].includes(Number(slot.role))).length;

  return (
    <div className="skills-editor">
      <div className="skills-summary">
        <div><strong>{slots.length}</strong><span>configured skills</span></div>
        <div className={movementCount === 1 ? "good" : "warn"}><strong>{movementCount}</strong><span>movement binding</span></div>
        <div><strong>{actionCount}</strong><span>combat actions</span></div>
      </div>
      {movementCount !== 1 && (
        <div className="inline-callout warn">
          <strong>{movementCount === 0 ? "Movement is not assigned." : "More than one movement skill is assigned."}</strong>{" "}
          Choose Movement as the purpose of exactly one binding.
        </div>
      )}
      <div className="skills-list">
        {slots.map((slot, index) => (
          <SkillRow
            key={index}
            slot={slot}
            onPatch={(patch) => updateSlot(index, patch)}
            onRemove={() => setSlots(slots.filter((_, i) => i !== index))}
          />
        ))}
      </div>
      {showDetected && (
        <DetectedSkillsPanel
          importedGemIds={new Set(slots.map((slot) => Number(slot.gemId)).filter(Boolean))}
          onImport={importDetected}
          onClose={() => setShowDetected(false)}
        />
      )}
      <div className="skill-add-row">
        <button type="button" className="skill-add primary-add" onClick={() => setShowDetected(true)}>+ Import detected skill</button>
        <button
          type="button"
          className="skill-add"
          title="Add an empty skill binding to fill in manually"
          onClick={() => setSlots([
            ...slots,
            { name: "New skill", vk: 0, role: 0, canCrossGaps: false, minCastIntervalMs: 100, maxRangeGrid: 30, chargeCount: 1, chargeRechargeMs: 3000, gemId: 0 },
          ])}
        >
          + Add manually
        </button>
      </div>
    </div>
  );
}

function SkillRow({ slot, onPatch, onRemove }: {
  slot: SkillSlot;
  onPatch: (patch: Partial<SkillSlot>) => void;
  onRemove: () => void;
}) {
  const [advanced, setAdvanced] = useState(false);
  const role = ROLES[Number(slot.role)] ?? ROLES[0];

  return (
    <div className="skill-card">
      <div className="skill-card-main">
        <label className="skill-primary-field">
          <span>Skill</span>
          <input className="skill-name" type="text" placeholder="Skill name" value={slot.name ?? ""} onChange={(e) => onPatch({ name: e.target.value })} />
        </label>
        <label className="skill-primary-field">
          <span>Key</span>
          <KeycodeButton value={slot.vk} onChange={(vk) => onPatch({ vk })} />
        </label>
        <label className="skill-primary-field skill-purpose">
          <span>Purpose</span>
          <select value={slot.role} onChange={(e) => onPatch({ role: parseInt(e.target.value, 10) })}>
            {ROLES.map((option, index) => <option key={option.name} value={index}>{option.name}</option>)}
          </select>
        </label>
        <button type="button" className="skill-remove" title="Remove skill" aria-label={`Remove ${slot.name || "skill"}`} onClick={onRemove}>x</button>
      </div>
      <div className="skill-role-hint">{role.hint}</div>
      {Number(slot.role) === ROLE_DASH && (
        <label className="skill-flag skill-gap-toggle">
          <input type="checkbox" checked={!!slot.canCrossGaps} onChange={(e) => onPatch({ canCrossGaps: e.target.checked })} />
          This dash can cross gaps in terrain
        </label>
      )}
      <button type="button" className="skill-advanced-toggle" onClick={() => setAdvanced(!advanced)}>
        {advanced ? "Hide timing details" : "Timing, range & charges"}
      </button>
      {advanced && (
        <div className="skill-advanced-grid">
          <NumField label="Minimum interval (ms)" value={slot.minCastIntervalMs} onChange={(v) => onPatch({ minCastIntervalMs: v })} />
          <NumField label="Effective range (grid)" value={slot.maxRangeGrid} onChange={(v) => onPatch({ maxRangeGrid: v })} />
          <NumField label="Detected gem ID" value={Number(slot.gemId ?? 0)} onChange={(v) => onPatch({ gemId: v })} />
          {Number(slot.role) === ROLE_DASH && (
            <>
              <NumField label="Charges" value={slot.chargeCount} onChange={(v) => onPatch({ chargeCount: v })} />
              <NumField label="Recharge (ms)" value={slot.chargeRechargeMs} onChange={(v) => onPatch({ chargeRechargeMs: v })} />
            </>
          )}
        </div>
      )}
    </div>
  );
}

export function NumField({ label, value, onChange, float = false }: {
  label: string;
  value: number;
  onChange: (value: number) => void;
  float?: boolean;
}) {
  return (
    <label className="skill-num">
      <span>{label}</span>
      <input type="number" step={float ? 0.05 : 1} value={value} onChange={(e) => onChange(float ? parseFloat(e.target.value) || 0 : parseInt(e.target.value, 10) || 0)} />
    </label>
  );
}

function DetectedSkillsPanel({ importedGemIds, onImport, onClose }: {
  importedGemIds: Set<number>;
  onImport: (entry: LiveSkill) => void;
  onClose: () => void;
}) {
  const liveSkills = useStatusStore((state) => state.status?.liveSkills);

  if (!liveSkills || liveSkills.length === 0) {
    return (
      <div className="skills-detected-wrap">
        <div className="detected-empty">No skills detected. Log into a character and keep its skill bar visible.</div>
        <button type="button" className="skill-add detected-close" onClick={onClose}>Close</button>
      </div>
    );
  }

  const visible = liveSkills.filter((entry) => entry.barSlot < 8);
  const extras = liveSkills.filter((entry) => entry.barSlot >= 8);

  return (
    <div className="skills-detected-wrap">
      <div className="detected-head">Detected on the visible skill bar</div>
      <DetectedGrid entries={visible} importedGemIds={importedGemIds} onImport={onImport} />
      {extras.length > 0 && (
        <>
          <div className="detected-head detected-head-sub">Extra or modifier-bound skills</div>
          <DetectedGrid entries={extras} importedGemIds={importedGemIds} onImport={onImport} />
        </>
      )}
      <button type="button" className="skill-add detected-close" onClick={onClose}>Done importing</button>
    </div>
  );
}

function DetectedGrid({ entries, importedGemIds, onImport }: {
  entries: LiveSkill[];
  importedGemIds: Set<number>;
  onImport: (entry: LiveSkill) => void;
}) {
  return (
    <div className="detected-grid">
      {entries.map((entry) => {
        const def = SLOT_DEFAULT_KEY[entry.barSlot];
        const keyLabel = def?.label ?? `slot ${entry.barSlot}`;
        const name = entry.name || `Skill #${entry.gemId}`;
        const imported = importedGemIds.has(Number(entry.gemId));
        return (
          <div className="detected-card" key={`${entry.barSlot}:${entry.gemId}`}>
            <div className="d-key">{keyLabel}</div>
            <div className="d-name">{name} <span className={`d-ready ${entry.isReady ? "good" : "warn"}`}>{entry.isReady ? "ready" : "busy"}</span></div>
            <div className="d-meta">gem {entry.gemId}{entry.maxUses ? ` / ${entry.maxUses} uses` : ""}</div>
            {imported ? <span className="d-imported">Imported</span> : <button type="button" className="d-import" onClick={() => onImport(entry)}>+ Import</button>}
          </div>
        );
      })}
    </div>
  );
}
