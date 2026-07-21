import type { FlaskSlot, SlotProfile } from "../../../api/types";
import { KeycodeButton } from "./KeycodeButton";
import { NumField } from "./SkillsEditor";

const FLASK_TRIGGERS = ["Disabled", "Hp", "Mana", "Time", "BuffMissing"];
const TRIGGER_HP = 1;
const TRIGGER_MANA = 2;
const TRIGGER_TIME = 3;
const TRIGGER_BUFF_MISSING = 4;

interface Props {
  value: SlotProfile<FlaskSlot> | undefined;
  onChange: (value: SlotProfile<FlaskSlot>) => void;
}

export function FlasksEditor({ value, onChange }: Props) {
  const profile: SlotProfile<FlaskSlot> = value ?? { slots: [] };
  const slots = profile.slots ?? [];

  const setSlots = (next: FlaskSlot[]) => onChange({ ...profile, slots: next });
  const updateSlot = (index: number, patch: Partial<FlaskSlot>) =>
    setSlots(slots.map((slot, i) => (i === index ? { ...slot, ...patch } : slot)));

  return (
    <div className="skills-editor">
      <div className="skills-list">
        {slots.map((slot, index) => (
          <div className="skill-row" key={index}>
            <input
              type="text"
              className="skill-name"
              placeholder="Name"
              value={slot.name ?? ""}
              onChange={(e) => updateSlot(index, { name: e.target.value })}
            />
            <KeycodeButton value={slot.vk} onChange={(vk) => updateSlot(index, { vk })} />
            <select
              value={slot.trigger}
              onChange={(e) => updateSlot(index, { trigger: parseInt(e.target.value, 10) })}
            >
              {FLASK_TRIGGERS.map((trigger, i) => (
                <option key={i} value={i}>{trigger}</option>
              ))}
            </select>
            {Number(slot.trigger) === TRIGGER_HP && (
              <NumField label="HP <" value={slot.hpThreshold} float onChange={(v) => updateSlot(index, { hpThreshold: v })} />
            )}
            {Number(slot.trigger) === TRIGGER_MANA && (
              <NumField label="Mana <" value={slot.manaThreshold} float onChange={(v) => updateSlot(index, { manaThreshold: v })} />
            )}
            {Number(slot.trigger) === TRIGGER_TIME && (
              <NumField label="interval ms" value={slot.intervalMs} onChange={(v) => updateSlot(index, { intervalMs: v })} />
            )}
            {Number(slot.trigger) === TRIGGER_BUFF_MISSING && (
              <input
                type="text"
                placeholder="buff name"
                value={slot.buffName ?? ""}
                onChange={(e) => updateSlot(index, { buffName: e.target.value })}
              />
            )}
            <NumField label="cooldown ms" value={slot.cooldownMs} onChange={(v) => updateSlot(index, { cooldownMs: v })} />
            <button
              type="button"
              className="skill-remove"
              title="Remove"
              onClick={() => setSlots(slots.filter((_, i) => i !== index))}
            >
              ×
            </button>
          </div>
        ))}
      </div>
      <button
        type="button"
        className="skill-add"
        onClick={() =>
          setSlots([
            ...slots,
            { name: "Flask", vk: 0, trigger: 0, hpThreshold: 0.6, manaThreshold: 0.3, intervalMs: 5000, buffName: "", cooldownMs: 4500 },
          ])
        }
      >
        + add flask
      </button>
    </div>
  );
}
