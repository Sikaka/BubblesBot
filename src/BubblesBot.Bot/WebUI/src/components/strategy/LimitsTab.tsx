import type { FarmingStrategy } from "../../api/strategy";
import { NullableNumberField, NumberField } from "./fields";

/** Per-run limits + safety. Shared by the strategy editor and the setup wizard. */
export function LimitsTab({ strategy, onChange }: { strategy: FarmingStrategy; onChange: (s: FarmingStrategy) => void }) {
  const setLimits = (patch: Partial<FarmingStrategy["limits"]>) => onChange({ ...strategy, limits: { ...strategy.limits, ...patch } });
  return (
    <>
      <NullableNumberField
        label="Max minutes per zone"
        hint="Per-zone stuck failsafe. Override the profile value, or 0 to disable."
        value={strategy.limits.maxZoneMinutes}
        fallback={8}
        min={0}
        max={60}
        onChange={(maxZoneMinutes) => setLimits({ maxZoneMinutes })}
      />
      <NumberField
        label="Max mechanic stalls per map"
        hint="Bounded interaction retries before a mechanic is abandoned with an incident."
        value={strategy.limits.maxMechanicStallsPerMap}
        min={0}
        max={20}
        onChange={(maxMechanicStallsPerMap) => setLimits({ maxMechanicStallsPerMap })}
      />
    </>
  );
}
