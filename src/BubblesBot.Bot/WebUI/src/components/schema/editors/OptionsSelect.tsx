interface Props {
  options: { label: string; value: number }[];
  value: number;
  onChange: (value: number) => void;
}

export function OptionsSelect({ options, value, onChange }: Props) {
  return (
    <select value={value} onChange={(e) => onChange(parseInt(e.target.value, 10))}>
      {options.map((option) => (
        <option key={option.value} value={option.value}>{option.label}</option>
      ))}
    </select>
  );
}
