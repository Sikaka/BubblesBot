import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { ApiError } from "../../api/client";
import { activateStrategy, listStrategies } from "../../api/strategyClient";
import type { StrategySummary } from "../../api/strategy";

/** Atlas-only strategy selector. Hidden for Overlay, Blight, and Simulacrum modes. */
export function StrategySwitcher() {
  const [strategies, setStrategies] = useState<StrategySummary[]>([]);
  const [activeId, setActiveId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const refresh = () =>
    listStrategies()
      .then((list) => { setStrategies(list.strategies); setActiveId(list.activeId); })
      .catch((e) => setError(String(e)));

  useEffect(() => { void refresh(); }, []);

  const activate = async (id: string) => {
    if (!id) return;
    setBusy(true);
    setError(null);
    try {
      await activateStrategy(id);
      await refresh();
    } catch (e) {
      if (e instanceof ApiError && e.body && typeof e.body === "object" && "errors" in e.body) {
        setError((e.body as { errors: string[] }).errors.join("; "));
      } else {
        setError(String(e));
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="card">
      <span className="setup-kicker">Atlas configuration</span>
      <h2>Atlas strategy</h2>
      <div className="arm-row">
        <select value={activeId ?? ""} disabled={busy} onChange={(e) => activate(e.target.value)}>
          <option value="" disabled>Select a strategy…</option>
          {strategies.map((s) => (
            <option key={s.id} value={s.id} disabled={!s.valid}>
              {s.name}{s.valid ? "" : " (invalid)"}
            </option>
          ))}
        </select>
        <Link className="btn-secondary sm" to="/strategies">Manage Atlas strategies</Link>
      </div>
      {error && <div className="arm-hint v bad">{error}</div>}
      {!activeId && <div className="arm-hint">No Atlas strategy selected — Atlas mode will refuse to arm until one is selected.</div>}
    </section>
  );
}
