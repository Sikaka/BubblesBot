import { useMemo, useState } from "react";

interface MapMod {
  key: string;
  category: string;
  name: string;
  defaultPolicy: number;
}

interface Policy { label: string; value: number }

interface Props {
  mods: MapMod[];
  policies: Policy[];
  value: string[];
  onChange: (value: string[]) => void;
}

function parseOverrides(entries: string[]): Map<string, number> {
  const result = new Map<string, number>();
  for (const row of entries) {
    const equals = row.indexOf("=");
    if (equals <= 0) continue;
    const policy = Number.parseInt(row.slice(equals + 1), 10);
    if (!Number.isNaN(policy)) result.set(row.slice(0, equals).trim(), policy);
  }
  return result;
}

/** Searchable, category-grouped build policy for the semantic map-modifier catalog. */
export function MapModTableEditor({ mods, policies, value, onChange }: Props) {
  const [search, setSearch] = useState("");
  const overrides = parseOverrides(value);
  const visible = useMemo(() => {
    const needle = search.trim().toLocaleLowerCase();
    return needle.length === 0 ? mods : mods.filter((mod) =>
      mod.name.toLocaleLowerCase().includes(needle)
      || mod.key.toLocaleLowerCase().includes(needle)
      || mod.category.toLocaleLowerCase().includes(needle));
  }, [mods, search]);

  const groups = useMemo(() => {
    const grouped = new Map<string, MapMod[]>();
    for (const mod of visible) {
      const rows = grouped.get(mod.category) ?? [];
      rows.push(mod);
      grouped.set(mod.category, rows);
    }
    return [...grouped.entries()];
  }, [visible]);

  const setPolicy = (mod: MapMod, policy: number) => {
    const next = new Map(overrides);
    if (policy === mod.defaultPolicy) next.delete(mod.key);
    else next.set(mod.key, policy);
    onChange(mods.flatMap((known) => {
      const selected = next.get(known.key);
      return selected === undefined || selected === known.defaultPolicy
        ? [] : [`${known.key}=${selected}`];
    }));
  };

  return (
    <div className="mapmodtable">
      <div className="mapmodtable-toolbar">
        <input
          type="search"
          value={search}
          placeholder="Search modifiers, categories, or keys…"
          onChange={(event) => setSearch(event.target.value)}
        />
        <span>{visible.length}/{mods.length} modifiers</span>
      </div>
      {groups.map(([category, rows]) => (
        <div className="mapmodtable-group" key={category}>
          <div className="mapmodtable-category">{category}</div>
          {rows.map((mod) => {
            const effective = overrides.get(mod.key) ?? mod.defaultPolicy;
            const overridden = overrides.has(mod.key);
            return (
              <div className={`mapmodtable-row ${overridden ? "overridden" : ""}`} key={mod.key}>
                <div>
                  <div className="mapmodtable-name">{mod.name}</div>
                  <div className="mapmodtable-key">{mod.key}</div>
                </div>
                <select value={effective} onChange={(event) =>
                  setPolicy(mod, Number.parseInt(event.target.value, 10))}>
                  {policies.map((policy) => (
                    <option value={policy.value} key={policy.value}>{policy.label}</option>
                  ))}
                </select>
                <button
                  type="button"
                  disabled={!overridden}
                  title="Reset to catalog default"
                  onClick={() => setPolicy(mod, mod.defaultPolicy)}
                >↺</button>
              </div>
            );
          })}
        </div>
      ))}
      {visible.length === 0 && <div className="tree-empty">No modifiers match this search.</div>}
    </div>
  );
}
