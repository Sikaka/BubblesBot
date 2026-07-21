interface Props {
  value: boolean;
  onChange: (value: boolean) => void;
}

export function BoolSwitch({ value, onChange }: Props) {
  return (
    <label className="switch">
      <input type="checkbox" checked={value} onChange={(e) => onChange(e.target.checked)} />
      <span className="slider" />
    </label>
  );
}
