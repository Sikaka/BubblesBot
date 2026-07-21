import type { SchemaField } from "../../../api/types";

interface Props {
  field: SchemaField;
  value: number | undefined;
  onChange: (value: number) => void;
}

/** Slider when [SettingRange] bounds exist, plain number input otherwise. */
export function NumberControl({ field, value, onChange }: Props) {
  const parse = (raw: string) =>
    field.type === "int" ? parseInt(raw, 10) || 0 : parseFloat(raw) || 0;

  if (field.min != null && field.max != null) {
    const current = value ?? field.min;
    return (
      <>
        <input
          type="range"
          min={field.min}
          max={field.max}
          step={field.step ?? 1}
          value={current}
          onChange={(e) => onChange(parse(e.target.value))}
        />
        <span className="range-display">{current}</span>
      </>
    );
  }
  return (
    <input
      type="number"
      value={value ?? 0}
      onChange={(e) => onChange(parse(e.target.value))}
    />
  );
}
