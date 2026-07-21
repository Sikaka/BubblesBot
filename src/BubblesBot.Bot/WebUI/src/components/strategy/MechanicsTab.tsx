import type {
  DeliriumBlock, EldritchAltarsBlock, FarmingStrategy, MechanicBlock, MechanicType, RitualBlock, UltimatumBlock,
} from "../../api/strategy";
import { MECHANIC_LABELS } from "../../api/strategy";
import { NumberField, SelectField, ToggleField } from "./fields";

interface Props {
  strategy: FarmingStrategy;
  onChange: (next: FarmingStrategy) => void;
  /** Optional focus: render only this mechanic's editor (used by the flowchart policy drawer). */
  only?: MechanicType;
}

export function MechanicsTab({ strategy, onChange, only }: Props) {
  const setBlock = (index: number, patch: Partial<MechanicBlock>) => {
    const mechanics = strategy.mechanics.map((b, i) => (i === index ? { ...b, ...patch } as MechanicBlock : b));
    onChange({ ...strategy, mechanics });
  };

  const blocks = strategy.mechanics
    .map((block, index) => ({ block, index }))
    .filter(({ block }) => !only || block.type === only);

  if (blocks.length === 0) return <div className="tree-empty">No mechanics in this strategy.</div>;

  return (
    <div className="mechanics-list">
      {blocks.map(({ block, index }) => (
        <div className={`mechanic-block ${block.enabled ? "" : "disabled"}`} key={block.type}>
          <div className="mechanic-block-head">
            <span className="mechanic-title">{MECHANIC_LABELS[block.type]}</span>
            <label className="switch">
              <input
                type="checkbox"
                checked={block.enabled}
                onChange={(e) => setBlock(index, { enabled: e.target.checked })}
              />
              <span className="slider" />
            </label>
          </div>
          {block.enabled && (
            <div className="mechanic-block-body">
              <NumberField
                label="Sweep bias (grid)"
                hint="Positive = win roughly-equidistant sweep ties against loot and other mechanics."
                value={block.sweepBias}
                min={-100}
                max={100}
                step={5}
                onChange={(v) => setBlock(index, { sweepBias: v })}
              />
              {block.type === "eldritchAltars" && (
                <AltarPolicyFields block={block} onChange={(patch) => setBlock(index, patch)} />
              )}
              {block.type === "ritual" && (
                <RitualPolicyFields block={block} onChange={(patch) => setBlock(index, patch)} />
              )}
              {block.type === "ultimatum" && (
                <UltimatumPolicyFields block={block} onChange={(patch) => setBlock(index, patch)} />
              )}
              {block.type === "delirium" && (
                <DeliriumPolicyFields block={block} onChange={(patch) => setBlock(index, patch)} />
              )}
            </div>
          )}
        </div>
      ))}
    </div>
  );
}

function DeliriumPolicyFields({ block, onChange }: {
  block: DeliriumBlock; onChange: (patch: Partial<DeliriumBlock>) => void;
}) {
  return (
    <>
      <NumberField label="Initial fog lead (grid)" value={block.initialFogLeadGrid} min={10} max={250} step={5}
        onChange={(initialFogLeadGrid) => onChange({ initialFogLeadGrid })} />
      <NumberField label="Maximum forward pace (grid/s)"
        hint="The bot pauses when radial progress from the mirror exceeds this average pace."
        value={block.maxForwardGridPerSecond} min={1} max={100}
        onChange={(maxForwardGridPerSecond) => onChange({ maxForwardGridPerSecond })} />
      <NumberField label="Maximum RF pack dwell (seconds)"
        hint="During Delirium, seek a forward dense pack, burn it briefly, then keep moving."
        value={block.maximumPackDwellSeconds} min={1} max={15} step={0.5}
        onChange={(maximumPackDwellSeconds) => onChange({ maximumPackDwellSeconds })} />
      <NumberField label="Minimum reward wait (seconds)"
        hint="Protects against delayed end-of-encounter reward materialisation."
        value={block.minimumRewardWaitSeconds} min={5} max={120} step={5}
        onChange={(minimumRewardWaitSeconds) => onChange({ minimumRewardWaitSeconds })} />
      <NumberField label="Reward quiet window (seconds)" value={block.rewardQuietSeconds} min={1} max={15} step={0.5}
        onChange={(rewardQuietSeconds) => onChange({ rewardQuietSeconds })} />
      <NumberField label="Maximum reward wait (seconds)" value={block.maximumRewardWaitSeconds} min={30} max={180} step={5}
        onChange={(maximumRewardWaitSeconds) => onChange({ maximumRewardWaitSeconds })} />
    </>
  );
}

function UltimatumPolicyFields({ block, onChange }: {
  block: UltimatumBlock; onChange: (patch: Partial<UltimatumBlock>) => void;
}) {
  return (
    <>
      <ToggleField label="Exit after encounter"
        hint="End the map immediately after Ultimatum completes or is skipped."
        value={block.exitAfter} onChange={(exitAfter) => onChange({ exitAfter })} />
      <ToggleField label="Survive" value={block.doSurvive}
        onChange={(doSurvive) => onChange({ doSurvive })} />
      <ToggleField label="Kill enemies" value={block.doKillEnemies}
        onChange={(doKillEnemies) => onChange({ doKillEnemies })} />
      <ToggleField label="Defend the altar" value={block.doDefendAltar}
        onChange={(doDefendAltar) => onChange({ doDefendAltar })} />
      <ToggleField label="Stand in Stone Circles"
        hint="Keep off for the initial safety-first profile: the bot skips this type and leaves immediately."
        value={block.doStandInCircles}
        onChange={(doStandInCircles) => onChange({ doStandInCircles })} />
    </>
  );
}

function AltarPolicyFields({ block, onChange }: {
  block: EldritchAltarsBlock; onChange: (patch: Partial<EldritchAltarsBlock>) => void;
}) {
  return (
    <>
      <SelectField
        label="Choice policy"
        hint="Smart ranks by reward weight with hard vetoes (stays fail-closed until the option UI is live-proven)."
        value={block.policy}
        options={[
          { value: "skip", label: "Skip (never take)" },
          { value: "top", label: "Always top" },
          { value: "bottom", label: "Always bottom" },
          { value: "smart", label: "Smart (score choices)" },
        ]}
        onChange={(policy) => onChange({ policy })}
      />
      <ToggleField
        label="Defer choices until boss dead"
        hint="Requires boss-kill evidence support (rejected on activate until that ships)."
        value={block.deferChoicesUntilBossDead}
        onChange={(deferChoicesUntilBossDead) => onChange({ deferChoicesUntilBossDead })}
      />
    </>
  );
}

function RitualPolicyFields({ block, onChange }: {
  block: RitualBlock; onChange: (patch: Partial<RitualBlock>) => void;
}) {
  return (
    <>
      <ToggleField
        label="Defer until map sweep"
        hint="Run the chain after the sweep to maximize the resurrection pool. Active rituals resume immediately regardless."
        value={block.deferUntilMapSweep}
        onChange={(deferUntilMapSweep) => onChange({ deferUntilMapSweep })}
      />
      <SelectField
        label="Chain ordering"
        value={block.chainOrdering}
        options={[
          { value: "nearestFirst", label: "Nearest first" },
          { value: "cloisterCorpses", label: "Most tracked corpses first" },
        ]}
        onChange={(chainOrdering) => onChange({ chainOrdering })}
      />
      {block.chainOrdering === "cloisterCorpses" && (
        <>
          <div className="sfield">
            <label>Corpse monster fragment</label>
            <div className="desc">Metadata fragment counted for corpse ordering (e.g. DemonFemale for Cloister students).</div>
            <div className="ctl">
              <input
                type="text"
                value={block.corpseMonsterPathFragment}
                onChange={(e) => onChange({ corpseMonsterPathFragment: e.target.value })}
              />
            </div>
          </div>
          <NumberField label="Corpse radius (grid)" value={block.corpseRadiusGrid} min={5} max={120} step={5}
            onChange={(v) => onChange({ corpseRadiusGrid: v })} />
          <NumberField label="Density weight" hint="Strategy-weight bonus for corpse-monster packs during destination scoring."
            value={block.densityWeight} min={0} max={100}
            onChange={(v) => onChange({ densityWeight: v })} />
        </>
      )}
      <div className="mechanic-subsection">Favours shop</div>
      <ToggleField label="Buy rewards" value={block.shop.enabled}
        onChange={(enabled) => onChange({ shop: { ...block.shop, enabled } })} />
      {block.shop.enabled && (
        <>
          <NumberField label="Reroll threshold (chaos)" value={block.shop.rerollThresholdChaos} min={0} max={500}
            onChange={(v) => onChange({ shop: { ...block.shop, rerollThresholdChaos: v } })} />
          <NumberField label="Final buy minimum (chaos)" value={block.shop.finalBuyMinChaos} min={0} max={100} step={0.5}
            onChange={(v) => onChange({ shop: { ...block.shop, finalBuyMinChaos: v } })} />
          <NumberField label="Maximum rerolls" value={block.shop.maxRerolls} min={0} max={20}
            onChange={(v) => onChange({ shop: { ...block.shop, maxRerolls: v } })} />
        </>
      )}
    </>
  );
}
