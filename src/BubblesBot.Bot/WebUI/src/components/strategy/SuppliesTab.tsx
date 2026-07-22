import type { FarmingStrategy, MapRollingMode, MapSource, ScarabLine } from "../../api/strategy";
import { NullableNumberField, NumberField, SelectField, TextField, ToggleField } from "./fields";

const SUPPORTED_MAPS = ["City Square", "Strand", "Jungle Valley"] as const;

/** Supply recipe + between-map loot policy. Shared by the strategy editor and the setup wizard. */
export function SuppliesTab({ strategy, onChange }: { strategy: FarmingStrategy; onChange: (s: FarmingStrategy) => void }) {
  const setSupply = (patch: Partial<FarmingStrategy["supply"]>) => onChange({ ...strategy, supply: { ...strategy.supply, ...patch } });
  const setScarab = (index: number, patch: Partial<ScarabLine>) =>
    setSupply({ scarabs: strategy.supply.scarabs.map((s, i) => (i === index ? { ...s, ...patch } : s)) });
  const selectMap = (name: string) => onChange({
    ...strategy,
    supply: { ...strategy.supply, map: { ...strategy.supply.map, targetMapName: name } },
    mapPrep: { ...strategy.mapPrep, atlasNodeName: name },
  });
  const setRolling = (patch: Partial<FarmingStrategy["mapPrep"]["rolling"]>) => onChange({
    ...strategy,
    mapPrep: { ...strategy.mapPrep, rolling: { ...strategy.mapPrep.rolling, ...patch } },
  });
  const setRejectedStats = (text: string) => setRolling({
    rejectedStatIds: text.split(/[\s,]+/).map((part) => parseInt(part, 10)).filter((id) => Number.isFinite(id) && id > 0),
  });

  return (
    <>
      <div className="unverified-note">
        Stash tab names and the atlas node can't be checked until the bot is in-game. If a tab is
        missing at runtime the bot stops safely rather than running an unjuiced map.
      </div>
      <TextField label="Supplies tab name" value={strategy.supply.suppliesTabName} onChange={(v) => setSupply({ suppliesTabName: v })} />
      <TextField label="Dump tab name" value={strategy.supply.dumpTabName} onChange={(v) => setSupply({ dumpTabName: v })} />
      <SelectField<MapSource>
        label="Map source"
        hint="Atlas storage consumes the selected node's stored maps. Player inventory uses generic map keys and can restock them from the named Maps tab."
        value={strategy.supply.map.source}
        options={[
          { value: "atlasStorage", label: "Atlas storage" },
          { value: "playerInventory", label: "Player inventory / Maps tab" },
        ]}
        onChange={(source) => setSupply({ map: { ...strategy.supply.map, source } })}
      />
      <SelectField
        label="Target map"
        hint="These nodes have current-build coordinates and fail-closed identity checks."
        value={strategy.supply.map.targetMapName}
        options={SUPPORTED_MAPS.map((name) => ({ value: name, label: name }))}
        onChange={selectMap}
      />
      <ToggleField label="Restock carried maps from stash" value={strategy.supply.map.restockFromStash ?? false}
        onChange={(v) => setSupply({ map: { ...strategy.supply.map, restockFromStash: v } })} />
      {(strategy.supply.map.restockFromStash ?? false) && (
        <NumberField label="Carried map buffer" value={strategy.supply.map.carriedMapBuffer ?? 1} min={1} max={20}
          onChange={(v) => setSupply({ map: { ...strategy.supply.map, carriedMapBuffer: v } })} />
      )}
      <div className="mechanic-subsection">Map preparation</div>
      <SelectField<MapRollingMode>
        label="Rolling policy"
        hint="Scoured is live for inventory-fed white map keys. Rare modes are saved as policy but activation remains fail-closed until currency application is live-proven."
        value={strategy.mapPrep.rolling.mode}
        options={[
          { value: "none", label: "Use supplied map as-is" },
          { value: "scoured", label: "Scoured / white rarity" },
          { value: "rare", label: "Rare (configured, not executable)" },
          { value: "rareCorrupted", label: "Rare + corrupted (configured, not executable)" },
        ]}
        onChange={(mode) => setRolling({ mode })}
      />
      {strategy.mapPrep.rolling.mode !== "none" && strategy.mapPrep.rolling.mode !== "scoured" && (
        <>
          <TextField
            label="Rejected mod stat IDs"
            hint="Comma-separated Stats.dat identities. The roller will eventually scour and retry when any is present."
            value={(strategy.mapPrep.rolling.rejectedStatIds ?? []).join(", ")}
            onChange={setRejectedStats}
          />
          <NumberField
            label="Maximum roll attempts"
            value={strategy.mapPrep.rolling.maxAttempts ?? 20}
            min={1}
            max={100}
            onChange={(maxAttempts) => setRolling({ maxAttempts })}
          />
        </>
      )}
      <div className="mechanic-subsection">Scarab recipe (max 5 slots)</div>
      <div className="unverified-note">
        The recipe is a fail-closed device guard: the bot verifies the requested number of scarab
        slots before activation. Keep the configured scarabs in inventory or pre-stage them in
        the device; automatic splitting/insertion is not enabled yet.
      </div>
      {strategy.supply.scarabs.map((line, index) => (
        <div className="scarab-line" key={index}>
          <input type="text" placeholder="display name" value={line.displayName}
            onChange={(e) => setScarab(index, { displayName: e.target.value })} />
          <input type="text" placeholder="path fragment" value={line.pathFragment}
            onChange={(e) => setScarab(index, { pathFragment: e.target.value })} />
          <input type="number" min={0} max={5} value={line.countPerMap} title="count per map"
            onChange={(e) => setScarab(index, { countPerMap: parseInt(e.target.value, 10) || 0 })} />
          <button type="button" className="skill-remove" title="Remove"
            onClick={() => setSupply({ scarabs: strategy.supply.scarabs.filter((_, i) => i !== index) })}>×</button>
        </div>
      ))}
      <button type="button" className="skill-add"
        onClick={() => setSupply({ scarabs: [...strategy.supply.scarabs, { pathFragment: "", displayName: "", countPerMap: 1 }] })}>
        + add scarab
      </button>

      <div className="mechanic-subsection">Between-map loot</div>
      <ToggleField label="Deposit after each map" value={strategy.loot.depositAfterEachMap}
        onChange={(depositAfterEachMap) => onChange({ ...strategy, loot: { ...strategy.loot, depositAfterEachMap } })} />
      <NullableNumberField
        label="Local off-screen loot min chaos"
        hint="Override bounded mechanic-cleanup memory (currently Ritual). Never causes an end-of-map backtrack; 0 remembers every accepted local drop."
        value={strategy.loot.backtrackMinChaosOverride}
        fallback={0}
        min={0}
        max={1000}
        onChange={(backtrackMinChaosOverride) => onChange({ ...strategy, loot: { ...strategy.loot, backtrackMinChaosOverride } })}
      />
    </>
  );
}
