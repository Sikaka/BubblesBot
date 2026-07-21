import { useStatusStore } from "../../state/statusStore";

const SEVERITY_CLASS: Record<string, string> = { Warning: "warn", Error: "bad", Critical: "bad" };

export function EventsPanel() {
  const events = useStatusStore((s) => s.status?.events);
  return (
    <section className="card">
      <h2>
        Events <span className="card-hint">({events?.length ?? 0})</span>
      </h2>
      <div className="events">
        {(events ?? []).map((e) => (
          <div className="ev-row" key={e.seq}>
            <span className="ev-t">{e.t}</span>{" "}
            <span className="ev-cat">[{e.category}]</span>{" "}
            <span className={`ev-msg ${SEVERITY_CLASS[e.severity] ?? ""}`}>{e.message}</span>
          </div>
        ))}
      </div>
    </section>
  );
}
