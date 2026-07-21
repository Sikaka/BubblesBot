interface Props {
  value: string;
  onChange: (value: string) => void;
}

export function TextControl({ value, onChange }: Props) {
  return <input type="text" value={value} onChange={(e) => onChange(e.target.value)} />;
}
