import { useEffect, useState } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { ApiError } from "../api/client";
import { activateStrategy, getStrategy, saveStrategy } from "../api/strategyClient";
import type { FarmingStrategy } from "../api/strategy";
import { MechanicsTab } from "../components/strategy/MechanicsTab";
import { FlowchartView, type FlowEditTarget } from "../components/strategy/FlowchartView";
import { GeneralTab } from "../components/strategy/GeneralTab";
import { SuppliesTab } from "../components/strategy/SuppliesTab";
import { LimitsTab } from "../components/strategy/LimitsTab";

type Tab = "general" | "mechanics" | "supplies" | "limits" | "flowchart";
const TABS: { key: Tab; label: string }[] = [
  { key: "general", label: "General" },
  { key: "mechanics", label: "Mechanics" },
  { key: "supplies", label: "Supplies" },
  { key: "limits", label: "Limits" },
  { key: "flowchart", label: "Flowchart" },
];

export default function StrategyEditorPage() {
  const { id = "" } = useParams();
  const navigate = useNavigate();
  const [original, setOriginal] = useState<FarmingStrategy | null>(null);
  const [draft, setDraft] = useState<FarmingStrategy | null>(null);
  const [tab, setTab] = useState<Tab>("general");
  const [drawerTarget, setDrawerTarget] = useState<FlowEditTarget | null>(null);
  const [errors, setErrors] = useState<string[]>([]);
  const [warnings, setWarnings] = useState<string[]>([]);
  const [message, setMessage] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    getStrategy(id)
      .then((doc) => { setOriginal(doc); setDraft(doc); })
      .catch((e) => setMessage(String(e)));
  }, [id]);

  const dirty = draft !== null && original !== null && JSON.stringify(draft) !== JSON.stringify(original);

  const save = async (): Promise<boolean> => {
    if (!draft) return false;
    setBusy(true);
    setMessage(null);
    try {
      const result = await saveStrategy(id, draft);
      setOriginal(result.strategy);
      setDraft(result.strategy);
      setErrors(result.errors);
      setWarnings(result.warnings);
      return true;
    } catch (e) {
      setMessage(String(e));
      return false;
    } finally {
      setBusy(false);
    }
  };

  const saveAndActivate = async () => {
    if (!(await save())) return;
    setBusy(true);
    try {
      await activateStrategy(id);
      setMessage("Saved and selected for Atlas.");
      setErrors([]);
    } catch (e) {
      if (e instanceof ApiError && e.body && typeof e.body === "object" && "errors" in e.body) {
        setErrors((e.body as { errors: string[] }).errors);
        setMessage("Saved, but cannot activate:");
      } else {
        setMessage(String(e));
      }
    } finally {
      setBusy(false);
    }
  };

  if (!draft) return <section className="card"><h2>Strategy</h2><div className="tree-empty">{message ?? "loading…"}</div></section>;

  return (
    <>
      <section className="card">
        <div className="editor-head">
          <button type="button" className="btn-secondary sm" onClick={() => navigate("/strategies")}>← Back</button>
          <h2 className="editor-title">{draft.identity.name || "Untitled strategy"}</h2>
        </div>
        <div className="editor-tabs">
          {TABS.map((t) => (
            <button key={t.key} type="button" className={`editor-tab ${tab === t.key ? "active" : ""}`} onClick={() => setTab(t.key)}>
              {t.label}
            </button>
          ))}
        </div>

        <div className="editor-body">
          {tab === "general" && <GeneralTab strategy={draft} onChange={setDraft} />}
          {tab === "mechanics" && <MechanicsTab strategy={draft} onChange={setDraft} />}
          {tab === "supplies" && <SuppliesTab strategy={draft} onChange={setDraft} />}
          {tab === "limits" && <LimitsTab strategy={draft} onChange={setDraft} />}
          {tab === "flowchart" && (
            <FlowchartView strategy={draft} onEdit={setDrawerTarget} />
          )}
        </div>

        {(errors.length > 0 || warnings.length > 0 || message) && (
          <div className="editor-messages">
            {message && <div className="v">{message}</div>}
            {errors.map((e) => <div className="v bad" key={e}>✗ {e}</div>)}
            {warnings.map((w) => <div className="v warn" key={w}>⚠ {w}</div>)}
          </div>
        )}
      </section>

      {drawerTarget && (
        <div className="drawer-backdrop" onClick={() => setDrawerTarget(null)}>
          <div className="drawer" onClick={(e) => e.stopPropagation()}>
            <div className="drawer-head">
              <div>
                <span className="setup-kicker">Flowchart policy</span>
                <h2>{drawerTitle(drawerTarget)}</h2>
              </div>
              <button type="button" className="btn-secondary sm" onClick={() => setDrawerTarget(null)}>Close</button>
            </div>
            {drawerTarget.kind === "mechanic" && <MechanicsTab strategy={draft} onChange={setDraft} only={drawerTarget.mechanic} />}
            {drawerTarget.kind === "supplies" && <SuppliesTab strategy={draft} onChange={setDraft} />}
            {drawerTarget.kind === "completion" && <GeneralTab strategy={draft} onChange={setDraft} showIdentity={false} />}
            {drawerTarget.kind === "limits" && <LimitsTab strategy={draft} onChange={setDraft} />}
          </div>
        </div>
      )}

      <div className="dirty-bar">
        <div className="dirty-messages">
          <span className="dirty-note">{dirty ? "Unsaved changes" : "Saved"}</span>
        </div>
        <button type="button" className="btn-secondary" disabled={busy || !dirty} onClick={() => setDraft(original)}>Discard</button>
        <button type="button" className="btn-secondary" disabled={busy} onClick={save}>Save</button>
        <button type="button" className="btn-primary" disabled={busy} onClick={saveAndActivate}>Save &amp; select for Atlas</button>
      </div>
    </>
  );
}

function drawerTitle(target: FlowEditTarget): string {
  if (target.kind === "mechanic") return "Mechanic behavior";
  if (target.kind === "supplies") return "Preparation & supplies";
  if (target.kind === "completion") return "Completion rules";
  return "Safety limits";
}
