import { useState } from "react";

interface Props {
  value: string[];
  placeholder: string;
  onChange: (value: string[]) => void;
}

/** Add/remove row list for List<string> settings (allowlists, whitelists). */
export function StringListEditor({ value, placeholder, onChange }: Props) {
  const [pending, setPending] = useState("");

  const commit = () => {
    const trimmed = pending.trim();
    if (!trimmed) return;
    onChange([...value, trimmed]);
    setPending("");
  };

  return (
    <div className="stringlist">
      <div className="stringlist-rows">
        {value.map((entry, index) => (
          <div className="stringlist-row" key={index}>
            <input
              type="text"
              className="stringlist-input"
              value={entry}
              onChange={(e) => onChange(value.map((v, i) => (i === index ? e.target.value : v)))}
            />
            <button
              type="button"
              className="stringlist-rm"
              title="Remove"
              onClick={() => onChange(value.filter((_, i) => i !== index))}
            >
              ×
            </button>
          </div>
        ))}
      </div>
      <div className="stringlist-add">
        <input
          type="text"
          placeholder={placeholder}
          value={pending}
          onChange={(e) => setPending(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter") {
              e.preventDefault();
              commit();
            }
          }}
        />
        <button type="button" className="stringlist-addbtn" onClick={commit}>+ add</button>
      </div>
    </div>
  );
}
