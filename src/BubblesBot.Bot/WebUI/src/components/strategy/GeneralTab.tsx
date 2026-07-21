import type { FarmingStrategy } from "../../api/strategy";
import { NumberField, TextField, ToggleField } from "./fields";

/** Identity + completion knobs. Shared by the strategy editor and the setup wizard. */
export function GeneralTab({ strategy, onChange, showIdentity = true }: {
  strategy: FarmingStrategy;
  onChange: (s: FarmingStrategy) => void;
  showIdentity?: boolean;
}) {
  const setIdentity = (patch: Partial<FarmingStrategy["identity"]>) =>
    onChange({ ...strategy, identity: { ...strategy.identity, ...patch } });
  const setCompletion = (patch: Partial<FarmingStrategy["completion"]>) =>
    onChange({ ...strategy, completion: { ...strategy.completion, ...patch } });

  return (
    <>
      {showIdentity && (
        <>
          <TextField label="Name" value={strategy.identity.name} onChange={(name) => setIdentity({ name })} />
          <TextField label="Description" value={strategy.identity.description} onChange={(description) => setIdentity({ description })} />
          <TextField label="Author" value={strategy.identity.author} onChange={(author) => setIdentity({ author })} placeholder="(optional)" />
          <TextField label="Game version" value={strategy.identity.gameVersion} onChange={(gameVersion) => setIdentity({ gameVersion })} placeholder="e.g. 3.26" />
        </>
      )}
      <NumberField label="Target map count" hint="Stop after this many completed maps." value={strategy.completion.targetMaps} min={1} max={500}
        onChange={(targetMaps) => setCompletion({ targetMaps })} />
      <NumberField label="Exploration done %"
        hint="Reveal % at which the map counts as swept (rituals start, zone can finish). 100 = fully exhaust the frontier."
        value={strategy.completion.explorationDonePercent} min={50} max={100} step={5}
        onChange={(explorationDonePercent) => setCompletion({ explorationDonePercent })} />
      <ToggleField
        label="Require boss kill"
        hint="Gates completion on positive boss-death evidence. Rejected on activate until boss evidence ships."
        value={strategy.completion.requireBossKill}
        onChange={(requireBossKill) => setCompletion({ requireBossKill })}
      />
    </>
  );
}
