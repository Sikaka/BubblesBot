import { useStatusStore } from "../state/statusStore";

const LABELS = {
  connecting: "connecting…",
  live: "live",
  polling: "live (polling)",
  disconnected: "disconnected",
} as const;

const CLASSES = {
  connecting: "connecting",
  live: "ok",
  polling: "ok",
  disconnected: "bad",
} as const;

export function ConnectionPill() {
  const connection = useStatusStore((s) => s.connection);
  const profile = useStatusStore((s) => s.status?.profile ?? "");
  return (
    <div className={`conn-pill ${CLASSES[connection]}`} title={profile ? `profile: ${profile}` : undefined}>
      {LABELS[connection]}
    </div>
  );
}
