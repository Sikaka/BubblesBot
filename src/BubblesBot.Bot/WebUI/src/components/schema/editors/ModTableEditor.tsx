interface Mod {
  id: string;
  name: string;
  defaultDanger: number;
}

interface Tier {
  label: string;
  value: number;
}

interface Props {
  mods: Mod[];
  tiers: Tier[];
  /** Underlying storage: "ModId=N" entries, only for tiers differing from the default. */
  value: string[];
  onChange: (value: string[]) => void;
}

function parseOverrides(entries: string[]): Map<string, number> {
  const overrides = new Map<string, number>();
  for (const row of entries) {
    const eq = row.indexOf("=");
    if (eq <= 0) continue;
    const parsed = parseInt(row.slice(eq + 1), 10);
    if (!Number.isNaN(parsed)) overrides.set(row.slice(0, eq).trim(), parsed);
  }
  return overrides;
}

/** Ultimatum mod-danger override table: one row per known mod, tier dropdown + reset. */
export function ModTableEditor({ mods, tiers, value, onChange }: Props) {
  const overrides = parseOverrides(value);
  const effective = (mod: Mod) => overrides.get(mod.id) ?? mod.defaultDanger;

  const persist = (next: Map<string, number>) => {
    const rows: string[] = [];
    for (const mod of mods) {
      const tier = next.get(mod.id);
      if (tier === undefined || tier === mod.defaultDanger) continue;
      rows.push(`${mod.id}=${tier}`);
    }
    onChange(rows);
  };

  const setTier = (mod: Mod, tier: number) => {
    const next = new Map(overrides);
    if (tier === mod.defaultDanger) next.delete(mod.id);
    else next.set(mod.id, tier);
    persist(next);
  };

  return (
    <div className="modtable">
      <div className="modtable-head">
        <span>Modifier</span><span>id</span><span>Danger tier</span><span />
      </div>
      {mods.map((mod) => {
        const overridden = overrides.has(mod.id) && overrides.get(mod.id) !== mod.defaultDanger;
        const defaultLabel = tiers.find((t) => t.value === mod.defaultDanger)?.label ?? String(mod.defaultDanger);
        return (
          <div className={`modtable-row ${overridden ? "overridden" : ""}`} key={mod.id}>
            <span className="modtable-name">{mod.name}</span>
            <span className="modtable-id" title={mod.id}>{mod.id}</span>
            <select
              className="modtable-tier"
              value={effective(mod)}
              onChange={(e) => setTier(mod, parseInt(e.target.value, 10))}
            >
              {tiers.map((tier) => (
                <option key={tier.value} value={tier.value}>{tier.label}</option>
              ))}
            </select>
            <button
              type="button"
              className="modtable-reset"
              title={`Reset to default (${defaultLabel})`}
              onClick={() => setTier(mod, mod.defaultDanger)}
            >
              ↺
            </button>
          </div>
        );
      })}
    </div>
  );
}
