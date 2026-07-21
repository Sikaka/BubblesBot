import { StatusGrid } from "../components/dashboard/StatusGrid";
import { ArmControl } from "../components/dashboard/ArmControl";
import { StrategySwitcher } from "../components/dashboard/StrategySwitcher";
import { LoopPanel } from "../components/dashboard/LoopPanel";
import { TreePanel } from "../components/dashboard/TreePanel";
import { EventsPanel } from "../components/dashboard/EventsPanel";
import { LootPanel } from "../components/dashboard/LootPanel";
import { useStatusStore } from "../state/statusStore";

export default function DashboardPage() {
  const atlasMode = useStatusStore((s) => s.status?.activeMode === 4);
  return (
    <>
      <ArmControl />
      {atlasMode && <StrategySwitcher />}
      <section className="card">
        <h2>Status</h2>
        <StatusGrid />
      </section>
      <LoopPanel />
      <section className="card">
        <h2>Loot</h2>
        <LootPanel />
      </section>
      <section className="card">
        <h2>Behavior tree</h2>
        <TreePanel />
      </section>
      <EventsPanel />
    </>
  );
}
