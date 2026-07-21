import type { ReactNode } from "react";

/** Compact labeled-row primitives for the bespoke strategy editor. */

export function Row({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return (
    <div className="sfield">
      <label>{label}</label>
      {hint && <div className="desc">{hint}</div>}
      <div className="ctl">{children}</div>
    </div>
  );
}

export function TextField({ label, hint, value, onChange, placeholder }: {
  label: string; hint?: string; value: string; onChange: (v: string) => void; placeholder?: string;
}) {
  return (
    <Row label={label} hint={hint}>
      <input type="text" value={value} placeholder={placeholder} onChange={(e) => onChange(e.target.value)} />
    </Row>
  );
}

export function NumberField({ label, hint, value, onChange, min, max, step }: {
  label: string; hint?: string; value: number; onChange: (v: number) => void; min?: number; max?: number; step?: number;
}) {
  return (
    <Row label={label} hint={hint}>
      <input
        type="number"
        value={value}
        min={min}
        max={max}
        step={step ?? 1}
        onChange={(e) => onChange(step && step < 1 ? parseFloat(e.target.value) || 0 : parseInt(e.target.value, 10) || 0)}
      />
    </Row>
  );
}

/** Nullable number: a checkbox toggles "inherit profile" (null) vs an explicit value. */
export function NullableNumberField({ label, hint, value, onChange, fallback, min, max }: {
  label: string; hint?: string; value: number | null; onChange: (v: number | null) => void;
  fallback: number; min?: number; max?: number;
}) {
  const overridden = value !== null;
  return (
    <Row label={label} hint={hint}>
      <label className="skill-flag">
        <input
          type="checkbox"
          checked={overridden}
          onChange={(e) => onChange(e.target.checked ? fallback : null)}
        />
        {" override"}
      </label>
      {overridden && (
        <input
          type="number"
          value={value}
          min={min}
          max={max}
          onChange={(e) => onChange(parseFloat(e.target.value) || 0)}
        />
      )}
      {!overridden && <span className="desc">inherits profile value</span>}
    </Row>
  );
}

export function ToggleField({ label, hint, value, onChange }: {
  label: string; hint?: string; value: boolean; onChange: (v: boolean) => void;
}) {
  return (
    <Row label={label} hint={hint}>
      <label className="switch">
        <input type="checkbox" checked={value} onChange={(e) => onChange(e.target.checked)} />
        <span className="slider" />
      </label>
    </Row>
  );
}

export function SelectField<T extends string>({ label, hint, value, options, onChange }: {
  label: string; hint?: string; value: T; options: { value: T; label: string }[]; onChange: (v: T) => void;
}) {
  return (
    <Row label={label} hint={hint}>
      <select value={value} onChange={(e) => onChange(e.target.value as T)}>
        {options.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
      </select>
    </Row>
  );
}
