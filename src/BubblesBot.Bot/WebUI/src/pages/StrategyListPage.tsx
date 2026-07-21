import { useEffect, useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import { ApiError } from "../api/client";
import {
  activateStrategy, createStrategy, deleteStrategy, exportStrategyUrl, importStrategy,
  listStrategies, listTemplates,
} from "../api/strategyClient";
import type { StrategySummary, StrategyTemplate } from "../api/strategy";
import { MECHANIC_LABELS, type MechanicType } from "../api/strategy";
import { useStatusStore } from "../state/statusStore";

export default function StrategyListPage() {
  const navigate = useNavigate();
  const atlasRunning = useStatusStore((state) => state.status?.activeMode === 4);
  const [strategies, setStrategies] = useState<StrategySummary[]>([]);
  const [activeId, setActiveId] = useState<string | null>(null);
  const [templates, setTemplates] = useState<StrategyTemplate[]>([]);
  const [loadErrors, setLoadErrors] = useState<string[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const fileInput = useRef<HTMLInputElement>(null);

  const refresh = async () => {
    try {
      const [list, tpl] = await Promise.all([listStrategies(), listTemplates()]);
      setStrategies(list.strategies);
      setActiveId(list.activeId);
      setLoadErrors(list.loadErrors);
      setTemplates(tpl.templates);
      setError(null);
    } catch (e) {
      setError(String(e));
    }
  };

  useEffect(() => { void refresh(); }, []);

  const guard = async (action: () => Promise<unknown>) => {
    setBusy(true);
    setError(null);
    try {
      await action();
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

  const onCreate = async (templateId: string) => {
    setBusy(true);
    setError(null);
    try {
      const created = await createStrategy({ fromTemplate: templateId });
      navigate(`/strategies/${created.identity.id}`);
    } catch (e) {
      setError(String(e));
      setBusy(false);
    }
  };

  const onImportFile = async (file: File) => {
    const text = await file.text();
    await guard(() => importStrategy(text));
  };

  return (
    <>
      <section className="card">
        <span className="setup-kicker">Atlas mode only</span>
        <h2>Atlas strategies</h2>
        <p className="desc">Atlas strategies configure maps, scarabs, league mechanics, exploration, and completion. They are not used by Overlay, Blight, or Simulacrum.</p>
        {error && <div className="v bad strategy-error">{error}</div>}
        {loadErrors.length > 0 && (
          <div className="v warn strategy-error">
            {loadErrors.length} strategy file(s) failed to load: {loadErrors.join("; ")}
          </div>
        )}
        <div className="strategy-toolbar">
          <select
            className="template-select"
            defaultValue=""
            disabled={busy}
            onChange={(e) => { if (e.target.value) void onCreate(e.target.value); e.target.value = ""; }}
          >
            <option value="" disabled>+ New from template…</option>
            {templates.map((t) => (
              <option key={t.templateId} value={t.templateId}>{t.name}</option>
            ))}
          </select>
          <button type="button" className="btn-secondary" disabled={busy} onClick={() => fileInput.current?.click()}>
            Import…
          </button>
          <input
            ref={fileInput}
            type="file"
            accept=".json,application/json"
            style={{ display: "none" }}
            onChange={(e) => { const f = e.target.files?.[0]; if (f) void onImportFile(f); e.target.value = ""; }}
          />
        </div>
      </section>

      {strategies.length === 0 ? (
        <section className="card"><div className="tree-empty">No strategies yet — create one from a template above.</div></section>
      ) : (
        strategies.map((s) => (
          <StrategyCard
            key={s.id}
            strategy={s}
            isSelected={s.id === activeId}
            atlasRunning={atlasRunning}
            busy={busy}
            onEdit={() => navigate(`/strategies/${s.id}`)}
            onActivate={() => guard(() => activateStrategy(s.id))}
            onDuplicate={() => guard(() => createStrategy({ fromStrategy: s.id }))}
            onDelete={() => { if (confirm(`Delete "${s.name}"?`)) void guard(() => deleteStrategy(s.id)); }}
          />
        ))
      )}
    </>
  );
}

function StrategyCard({ strategy, isSelected, atlasRunning, busy, onEdit, onActivate, onDuplicate, onDelete }: {
  strategy: StrategySummary;
  isSelected: boolean;
  atlasRunning: boolean;
  busy: boolean;
  onEdit: () => void;
  onActivate: () => void;
  onDuplicate: () => void;
  onDelete: () => void;
}) {
  return (
    <section className={`card strategy-card ${isSelected ? "active" : ""}`}>
      <div className="strategy-head">
        <div>
          <span className="strategy-name">{strategy.name}</span>
          {isSelected && <span className="strategy-badge good">{atlasRunning ? "active in Atlas" : "selected for Atlas"}</span>}
          {!strategy.valid && <span className="strategy-badge bad">invalid</span>}
        </div>
        <div className="strategy-actions">
          {!isSelected && (
            <button type="button" className="btn-primary sm" disabled={busy || !strategy.valid} onClick={onActivate}>
              Select for Atlas
            </button>
          )}
          <button type="button" className="btn-secondary sm" disabled={busy} onClick={onEdit}>Edit</button>
          <button type="button" className="btn-secondary sm" disabled={busy} onClick={onDuplicate}>Duplicate</button>
          <a className="btn-secondary sm" href={exportStrategyUrl(strategy.id)} download>Export</a>
          <button
            type="button"
            className="btn-secondary sm danger"
            disabled={busy || isSelected}
            title={isSelected ? "Select another Atlas strategy before deleting" : undefined}
            onClick={onDelete}
          >
            Delete
          </button>
        </div>
      </div>
      {strategy.description && <div className="desc">{strategy.description}</div>}
      <div className="strategy-meta">
        <span>Map: <strong>{strategy.summary.mapName}</strong></span>
        <span>Target: <strong>{strategy.summary.targetMaps}</strong> maps</span>
        <span>
          Mechanics:{" "}
          {strategy.summary.mechanics.length > 0
            ? strategy.summary.mechanics.map((m) => MECHANIC_LABELS[m as MechanicType] ?? m).join(", ")
            : "none"}
        </span>
      </div>
    </section>
  );
}
