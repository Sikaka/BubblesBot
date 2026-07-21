import type { FarmingStrategy, ScarabLine } from "../../api/strategy";
import { NullableNumberField, NumberField, TextField, ToggleField } from "./fields";

/** Supply recipe + between-map loot policy. Shared by the strategy editor and the setup wizard. */
export function SuppliesTab({ strategy, onChange }: { strategy: FarmingStrategy; onChange: (s: FarmingStrategy) => void }) {
  const setSupply = (patch: Partial<FarmingStrategy["supply"]>) => onChange({ ...strategy, supply: { ...strategy.supply, ...patch } });
  const setScarab = (index: number, patch: Partial<ScarabLine>) =>
    setSupply({ scarabs: strategy.supply.scarabs.map((s, i) => (i === index ? { ...s, ...patch } : s)) });

  return (
    <>
      <div className="unverified-note">
        Stash tab names and the atlas node can't be checked until the bot is in-game. If a tab is
        missing at runtime the bot stops safely rather than running an unjuiced map.
      </div>
      <TextField label="Supplies tab name" value={strategy.supply.suppliesTabName} onChange={(v) => setSupply({ suppliesTabName: v })} />
      <TextField label="Dump tab name" value={strategy.supply.dumpTabName} onChange={(v) => setSupply({ dumpTabName: v })} />
      <TextField label="Target map name" value={strategy.supply.map.targetMapName}
        onChange={(v) => setSupply({ map: { ...strategy.supply.map, targetMapName: v } })} />
      <ToggleField label="Restock carried maps from stash" value={strategy.supply.map.restockFromStash ?? false}
        onChange={(v) => setSupply({ map: { ...strategy.supply.map, restockFromStash: v } })} />
      {(strategy.supply.map.restockFromStash ?? false) && (
        <NumberField label="Carried map buffer" value={strategy.supply.map.carriedMapBuffer ?? 1} min={1} max={20}
          onChange={(v) => setSupply({ map: { ...strategy.supply.map, carriedMapBuffer: v } })} />
      )}
      <TextField label="Atlas node" hint="Must be in the build's node catalog (City Square only, for now)."
        value={strategy.mapPrep.atlasNodeName}
        onChange={(v) => onChange({ ...strategy, mapPrep: { ...strategy.mapPrep, atlasNodeName: v } })} />

      <div className="mechanic-subsection">Scarab recipe (max 5 slots)</div>
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
        label="Backtrack min chaos"
        hint="Override the profile's backtrack threshold. 0 = remember every accepted label (stacked decks)."
        value={strategy.loot.backtrackMinChaosOverride}
        fallback={0}
        min={0}
        max={1000}
        onChange={(backtrackMinChaosOverride) => onChange({ ...strategy, loot: { ...strategy.loot, backtrackMinChaosOverride } })}
      />
    </>
  );
}
