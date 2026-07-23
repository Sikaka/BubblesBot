import { useEffect, useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import {
  ApiError, controlArm, controlMode, fetchMeta, fetchSchema, fetchSettings, patchSettings,
  type BotMeta, type FieldError, type PatchOp, type SettingsEnvelope,
} from "../api/client";
import type { Schema, Settings } from "../api/types";
import {
  activateStrategy, createStrategy, listTemplates, saveStrategy,
} from "../api/strategyClient";
import type { FarmingStrategy, StrategyTemplate } from "../api/strategy";
import { fieldPath, pathGet, pathSet, sameValue } from "../lib/paths";
import { SchemaForm } from "../components/schema/SchemaForm";
import { MechanicsTab } from "../components/strategy/MechanicsTab";
import { SuppliesTab } from "../components/strategy/SuppliesTab";
import { GeneralTab } from "../components/strategy/GeneralTab";
import { LimitsTab } from "../components/strategy/LimitsTab";
import { FlowchartView } from "../components/strategy/FlowchartView";
import { CombatSetup } from "../components/setup/CombatSetup";
import { BOT_MODES, modeDefinition, type BotModeId } from "../lib/modes";
import { reflectActiveMode, refreshStatusNow, useStatusStore } from "../state/statusStore";
import { clearWizardDraft, useWizardStore } from "../state/wizardStore";

type StepKind = "preflight" | "character" | "combat" | "skills" | "flasksMovement" | "archetype"
  | "modeSettings" | "mechanics" | "supplies" | "limits" | "review" | "arm";

type StepGroup = "start" | "character" | "farm" | "finish";
interface StepDef { kind: StepKind; title: string; group: StepGroup; atlasOnly?: boolean; dedicatedModeOnly?: boolean }

const STEPS: StepDef[] = [
  { kind: "preflight", title: "Welcome", group: "start" },
  { kind: "character", title: "Character", group: "character" },
  { kind: "combat", title: "Combat style", group: "character" },
  { kind: "skills", title: "Skill bindings", group: "character" },
  { kind: "flasksMovement", title: "Safety & movement", group: "character" },
  { kind: "archetype", title: "Run mode", group: "farm" },
  { kind: "modeSettings", title: "Mode settings", group: "farm", dedicatedModeOnly: true },
  { kind: "mechanics", title: "Atlas logic", group: "farm", atlasOnly: true },
  { kind: "supplies", title: "Atlas supplies", group: "farm", atlasOnly: true },
  { kind: "limits", title: "Atlas limits", group: "farm", atlasOnly: true },
  { kind: "review", title: "Review & save", group: "finish" },
  { kind: "arm", title: "Ready to run", group: "finish" },
];

const GROUP_LABELS: Record<StepGroup, string> = {
  start: "Get started",
  character: "Character setup",
  farm: "Farm setup",
  finish: "Finish",
};

export default function WizardPage() {
  const navigate = useNavigate();
  const store = useWizardStore();
  const [schema, setSchema] = useState<Schema | null>(null);
  const [original, setOriginal] = useState<Settings | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (store.settingsDraft) return;   // resuming a saved draft
    Promise.all([fetchSchema(), fetchSettings()])
      .then(([sc, envelope]) => {
        setSchema(sc);
        setOriginal(envelope.settings);
        useWizardStore.setState({ settingsDraft: envelope.settings, settingsVersion: envelope.version });
      })
      .catch((e) => setError(String(e)));
  }, [store.settingsDraft]);

  // Resuming a persisted draft: the draft (the user's edits) is restored from localStorage, but
  // the schema, the diff baseline, and the concurrency version must ALWAYS come fresh from the
  // running bot — the persisted draft never carries a trustworthy version, and a stale one causes
  // the Review-step save to 409 permanently. Refresh all three on mount regardless of the draft.
  useEffect(() => {
    if (!schema) fetchSchema().then(setSchema).catch((e) => setError(String(e)));
    fetchSettings()
      .then((envelope) => {
        setOriginal(envelope.settings);
        useWizardStore.setState({ settingsVersion: envelope.version });
      })
      .catch(() => {});
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Atlas gets strategy steps; Blight and Simulacrum get their dedicated settings; Overlay
  // needs neither. Keeping the branches exclusive prevents a stale Atlas strategy from being
  // presented as part of a Blight/Simulacrum setup.
  const steps = useMemo(
    () => STEPS.filter((candidate) => {
      if (candidate.atlasOnly) return store.selectedMode === 4 && store.strategyDraft !== null;
      if (candidate.dedicatedModeOnly) return store.selectedMode === 5 || store.selectedMode === 6;
      return true;
    }),
    [store.selectedMode, store.strategyDraft],
  );
  const stepIndex = Math.min(store.step, steps.length - 1);
  const step = steps[stepIndex];

  const setSettings = (path: string[], value: unknown) =>
    useWizardStore.setState((s) => ({ settingsDraft: s.settingsDraft ? pathSet(s.settingsDraft, path, value) : s.settingsDraft }));
  const setStrategy = (next: FarmingStrategy) => useWizardStore.setState({ strategyDraft: next });
  const applySettings = (values: Record<string, unknown>) => {
    useWizardStore.setState((state) => {
      if (!state.settingsDraft) return state;
      let next = state.settingsDraft;
      for (const [name, value] of Object.entries(values)) {
        const field = schema?.fields.find((candidate) => candidate.name === name);
        if (field) next = pathSet(next, fieldPath(field), value);
      }
      return { settingsDraft: next };
    });
  };

  const goto = (i: number) => useWizardStore.setState({ step: Math.max(0, Math.min(i, steps.length - 1)) });

  if (error) return <section className="card"><h2>Setup</h2><div className="v bad">{error}</div></section>;
  if (!schema || !store.settingsDraft) return <section className="card"><h2>Setup</h2><div className="tree-empty">loading…</div></section>;

  return (
    <>
      <WizardProgress steps={steps} stepIndex={stepIndex} onGoBack={goto} />

      <section className="card wizard-content-card">
        <h2>{step.title}</h2>
        {step.kind === "preflight" && <PreflightStep />}
        {step.kind === "character" && <CharacterStep />}
        {step.kind === "combat" && (
          <CombatSetup schema={schema} values={store.settingsDraft} saved={original ?? store.settingsDraft}
            onChange={setSettings} onApply={applySettings} />
        )}
        {step.kind === "skills" && (
          <>
            <div className="setup-lead compact">
              <span className="setup-kicker">Teach the bot your hotbar</span>
              <h3>What is each skill for?</h3>
              <p>Import detected skills, assign one movement binding, then label attacks, dashes, buffs, and marks by purpose.</p>
            </div>
            <SchemaForm fields={schema.fields} values={store.settingsDraft} saved={original ?? store.settingsDraft}
              onChange={setSettings} categories={["Skills"]} />
          </>
        )}
        {step.kind === "flasksMovement" && (
          <SchemaForm fields={schema.fields} values={store.settingsDraft} saved={original ?? store.settingsDraft}
            onChange={setSettings} categories={["Flasks", "Movement", "Exploration"]} />
        )}
        {step.kind === "archetype" && <ArchetypeStep onError={setError} />}
        {step.kind === "modeSettings" && (
          <ModeSettingsStep mode={store.selectedMode} schema={schema} values={store.settingsDraft}
            saved={original ?? store.settingsDraft} onChange={setSettings} />
        )}
        {step.kind === "mechanics" && store.strategyDraft && (
          <>
            <FlowchartView strategy={store.strategyDraft} />
            <div className="wizard-policy-divider"><span>Configure mechanic branches</span></div>
            <MechanicsTab strategy={store.strategyDraft} onChange={setStrategy} />
          </>
        )}
        {step.kind === "supplies" && store.strategyDraft && (
          <SuppliesTab strategy={store.strategyDraft} onChange={setStrategy} />
        )}
        {step.kind === "limits" && store.strategyDraft && (
          <>
            <GeneralTab strategy={store.strategyDraft} onChange={setStrategy} showIdentity={false} />
            <LimitsTab strategy={store.strategyDraft} onChange={setStrategy} />
          </>
        )}
        {step.kind === "review" && <ReviewStep schema={schema} original={original} onDone={() => goto(stepIndex + 1)} onError={setError} />}
        {step.kind === "arm" && <ArmStep onFinish={() => { clearWizardDraft(); navigate("/"); }} />}
      </section>

      <div className="dirty-bar wizard-actions">
        <button type="button" className="btn-secondary" onClick={() => { clearWizardDraft(); navigate("/"); }}>Exit setup</button>
        <span className="wizard-action-spacer" />
        <button type="button" className="btn-secondary" disabled={stepIndex === 0} onClick={() => goto(stepIndex - 1)}>Back</button>
        {step.kind !== "arm" && step.kind !== "review" && (
          <button type="button" className="btn-primary"
            disabled={step.kind === "archetype" && (store.selectedMode === null || (store.selectedMode === 4 && !store.strategyDraft))}
            onClick={() => goto(stepIndex + 1)}>
            Continue
          </button>
        )}
      </div>
    </>
  );
}

function WizardProgress({ steps, stepIndex, onGoBack }: {
  steps: StepDef[];
  stepIndex: number;
  onGoBack: (index: number) => void;
}) {
  const current = steps[stepIndex];
  const groupSteps = steps.filter((candidate) => candidate.group === current.group);
  const groupPosition = groupSteps.findIndex((candidate) => candidate.kind === current.kind) + 1;
  const progress = ((stepIndex + 1) / steps.length) * 100;
  return (
    <section className="card wizard-progress-card">
      <div className="wizard-progress-head">
        <div><span className="setup-kicker">{GROUP_LABELS[current.group]}</span><strong>{current.title}</strong></div>
        <span>{current.group === "start" ? "" : `${groupPosition} of ${groupSteps.length} / `}{stepIndex + 1} of {steps.length}</span>
      </div>
      <div className="wizard-progress-track"><span style={{ width: `${progress}%` }} /></div>
      <div className="wizard-steps">
        {steps.map((candidate, index) => (
          <button key={candidate.kind} type="button" disabled={index > stepIndex}
            className={`wizard-step ${index === stepIndex ? "active" : ""} ${index < stepIndex ? "done" : ""}`}
            onClick={() => index < stepIndex && onGoBack(index)}>
            <span>{index < stepIndex ? "OK" : index + 1}</span>{candidate.title}
          </button>
        ))}
      </div>
    </section>
  );
}

function PreflightStep() {
  const [meta, setMeta] = useState<BotMeta | null>(null);
  useEffect(() => { fetchMeta().then(setMeta).catch(() => {}); }, []);
  const check = (ok: boolean, label: string, detail?: string) => (
    <div className="preflight-row">
      <span className={ok ? "v good" : "v bad"}>{ok ? "✓" : "✗"}</span>
      <span>{label}{detail ? ` — ${detail}` : ""}</span>
    </div>
  );
  if (!meta) return <div className="tree-empty">checking…</div>;
  return (
    <>
      <p className="desc">This wizard configures your build profile and farming plan. Your character settings are not changed and no strategy is activated until Review.</p>
      {check(meta.gameAttached, "Game attached", meta.gameAttached ? (meta.gameProcessId ? `PID ${meta.gameProcessId}` : undefined) : "start PoE and relaunch the bot")}
      {check(meta.gateAvailable, "Game-state gate available")}
      {check(meta.gameState === "InGame", "In-world", meta.gameState)}
      {check(true, "Bot version", meta.botVersion)}
    </>
  );
}

function CharacterStep() {
  const status = useStatusStore((s) => s.status);
  return (
    <>
      <p className="desc">Settings are saved per character; the bot auto-switches profiles when you log in.</p>
      <div className="status-grid">
        <div className="status-row"><span className="k">Character</span><span className="v">{status?.character || "(log in to a character)"}</span></div>
        <div className="status-row"><span className="k">League</span><span className="v">{status?.league || "—"}</span></div>
        <div className="status-row"><span className="k">Profile</span><span className="v">{status?.profile || "—"}</span></div>
      </div>
    </>
  );
}

function ArchetypeStep({ onError }: { onError: (e: string) => void }) {
  const [templates, setTemplates] = useState<StrategyTemplate[]>([]);
  const selectedMode = useWizardStore((s) => s.selectedMode);
  const strategyDraft = useWizardStore((s) => s.strategyDraft);
  const [busy, setBusy] = useState(false);

  useEffect(() => { listTemplates().then((t) => setTemplates(t.templates)).catch((e) => onError(String(e))); }, [onError]);

  const chooseTemplate = async (templateId: string) => {
    setBusy(true);
    try {
      // Create the backing document now (with a fresh id); wizard edits it in memory and the
      // Review step saves + activates. Abandoning setup leaves an unconfigured draft strategy
      // the user can delete from the Atlas tab.
      const doc = await createStrategy({ fromTemplate: templateId });
      useWizardStore.setState({ strategyDraft: doc, selectedMode: 4 });
    } catch (e) {
      onError(String(e));
    } finally {
      setBusy(false);
    }
  };

  const chooseMode = (mode: BotModeId) => useWizardStore.setState({
    selectedMode: mode,
    strategyDraft: mode === 4 ? strategyDraft : null,
  });

  return (
    <>
      <div className="setup-lead compact">
        <span className="setup-kicker">One active mode</span>
        <h3>What should this character run?</h3>
        <p>Choose one of the four top-level modes. Atlas strategies only belong to Atlas; Blight and Simulacrum have their own settings.</p>
      </div>
      <div className="mode-grid setup-mode-grid">
        {BOT_MODES.map((mode) => (
          <button key={mode.id} type="button" className={`mode-card ${selectedMode === mode.id ? "active" : ""}`}
            onClick={() => chooseMode(mode.id)}>
            <span className="mode-card-check">{selectedMode === mode.id ? "Selected" : "Select"}</span>
            <strong>{mode.name}</strong>
            <span>{mode.shortDescription}</span>
            <small>{mode.detail}</small>
          </button>
        ))}
      </div>

      {selectedMode === 4 && (
        <div className="atlas-template-picker">
          <div className="wizard-policy-divider"><span>Choose an Atlas strategy template</span></div>
          <p className="desc">This controls map selection, scarabs, league mechanics, exploration, completion, and between-map supplies.</p>
          <div className="archetype-grid">
            {templates.map((template) => (
              <button key={template.templateId} type="button" disabled={busy}
                className={`archetype-card ${strategyDraft?.identity.name === template.name ? "chosen" : ""}`}
                onClick={() => chooseTemplate(template.templateId)}>
                <div className="archetype-name">{template.name}</div>
                <div className="desc">{template.description}</div>
              </button>
            ))}
          </div>
          {strategyDraft && <div className="v good archetype-selected">Atlas strategy: {strategyDraft.identity.name}</div>}
        </div>
      )}
      {selectedMode !== null && selectedMode !== 4 && (
        <div className="mode-selection-summary"><strong>{modeDefinition(selectedMode).name}</strong> selected. No Atlas strategy will be used in this mode.</div>
      )}
    </>
  );
}

function ModeSettingsStep({ mode, schema, values, saved, onChange }: {
  mode: number | null;
  schema: Schema;
  values: Settings;
  saved: Settings;
  onChange: (path: string[], value: unknown) => void;
}) {
  const categories = mode === 5 ? ["Blight", "Blight chests"]
    : mode === 6 ? ["Simulacrum"]
    : mode === 7 ? ["Guardian Rota"] : [];
  if (categories.length === 0) return null;
  return (
    <>
      <div className="setup-lead compact">
        <span className="setup-kicker">{modeDefinition(mode).name}</span>
        <h3>Configure this mode</h3>
        <p>These settings belong to {modeDefinition(mode).name}, not to an Atlas strategy.</p>
      </div>
      <SchemaForm fields={schema.fields} values={values} saved={saved} onChange={onChange} categories={categories} />
    </>
  );
}

function ReviewStep({ schema, original, onDone, onError }: {
  schema: Schema; original: Settings | null; onDone: () => void; onError: (e: string) => void;
}) {
  const store = useWizardStore();
  const [saving, setSaving] = useState(false);
  const [fieldErrors, setFieldErrors] = useState<FieldError[]>([]);
  const [saved, setSaved] = useState(false);

  // Diff the draft against a baseline by schema-field paths — those are exactly the settable
  // leaves the PATCH endpoint accepts. A generic recursive diff would emit sub-paths of complex
  // settings (e.g. skills.slots) that the patcher rejects as non-nested. The 409 rebase path
  // passes the fresh server settings the conflict handed back; the review display uses `original`.
  const opsAgainst = (base: Settings | null): PatchOp[] => {
    if (!store.settingsDraft || !base) return [];
    const ops: PatchOp[] = [];
    for (const field of schema.fields) {
      const path = fieldPath(field);
      const value = pathGet(store.settingsDraft, path);
      if (!sameValue(value, pathGet(base, path))) ops.push({ path, value });
    }
    return ops;
  };
  const settingsOps = (): PatchOp[] => opsAgainst(original);

  // Apply the build-scope settings with optimistic-concurrency recovery. A 409 means the server
  // published a change while the wizard was open (arm/disarm, profile switch, another tab). The
  // wizard is authoritative for the fields it edits, so rebase onto the fresh settings the 409
  // returns and retry once; a second 409 is surfaced rather than looping.
  const applySettingsDraft = async () => {
    let base = original;
    let version = store.settingsVersion;
    for (let attempt = 0; attempt < 2; attempt++) {
      const ops = opsAgainst(base);
      if (ops.length === 0) return;
      try {
        const applied = await patchSettings(ops, version);
        useWizardStore.setState({ settingsVersion: applied.version });
        return;
      } catch (e) {
        if (attempt === 0 && e instanceof ApiError && e.status === 409 && e.body && typeof e.body === "object") {
          const fresh = e.body as SettingsEnvelope;
          base = fresh.settings;
          version = fresh.version;
          useWizardStore.setState({ settingsVersion: fresh.version });
          continue;
        }
        throw e;
      }
    }
  };

  const save = async () => {
    setSaving(true);
    setFieldErrors([]);
    try {
      await applySettingsDraft();

      if (store.selectedMode === 4 && store.strategyDraft) {
        await saveStrategy(store.strategyDraft.identity.id, store.strategyDraft);
        await activateStrategy(store.strategyDraft.identity.id);
        await controlMode(4, true);
      } else if (store.selectedMode !== null) {
        await controlMode(store.selectedMode, true);
      }
      if (store.selectedMode !== null) {
        reflectActiveMode(store.selectedMode as BotModeId);
        await refreshStatusNow();
      }
      setSaved(true);
      onDone();
    } catch (e) {
      if (e instanceof ApiError && e.status === 422 && e.body && typeof e.body === "object" && "errors" in e.body) {
        const body = e.body as { errors: unknown };
        if (Array.isArray(body.errors) && typeof body.errors[0] === "object") setFieldErrors(body.errors as FieldError[]);
        else onError(Array.isArray(body.errors) ? (body.errors as string[]).join("; ") : String(e));
      } else {
        onError(String(e));
      }
    } finally {
      setSaving(false);
    }
  };

  const ops = settingsOps();
  return (
    <>
      <p className="desc">Confirm what will become active:</p>
      <div className="review-block">
        <strong>Build profile</strong> — {ops.length} setting change{ops.length === 1 ? "" : "s"} applied to the current character.
      </div>
      <div className="review-block">
        {store.selectedMode !== null
          ? <><strong>Mode</strong> — {modeDefinition(store.selectedMode).name}</>
          : <span className="v warn">No mode chosen — go back to Run mode.</span>}
      </div>
      {store.selectedMode === 4 && (
        <div className="review-block">
          {store.strategyDraft
            ? <><strong>Atlas strategy</strong> — "{store.strategyDraft.identity.name}" saved and selected.</>
            : <span className="v bad">Atlas requires a strategy template.</span>}
        </div>
      )}
      {store.selectedMode !== null && store.selectedMode !== 4 && (
        <div className="review-block"><strong>Atlas strategy</strong> — not used by {modeDefinition(store.selectedMode).name}.</div>
      )}
      {fieldErrors.map((fe) => <div className="v bad" key={fe.path}>✗ {fe.path}: {fe.message}</div>)}
      <button type="button" className="btn-primary" disabled={saving || saved} onClick={save}>
        {saving ? "Saving…" : saved ? "Saved ✓" : "Save & continue"}
      </button>
    </>
  );
}

function ArmStep({ onFinish }: { onFinish: () => void }) {
  const status = useStatusStore((s) => s.status);
  const selectedMode = useWizardStore((s) => s.selectedMode);
  const [result, setResult] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const arm = async () => {
    setBusy(true);
    try {
      const r = await controlArm();
      setResult(r.warnings.length > 0 ? `Armed with warnings: ${r.warnings.join("; ")}` : "Armed.");
      await refreshStatusNow();
      onFinish();
    } catch (e) {
      if (e instanceof ApiError && e.body && typeof e.body === "object" && "reasons" in e.body) {
        setResult(`Cannot arm: ${(e.body as { reasons: string[] }).reasons.join("; ")}`);
      } else {
        setResult(String(e));
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <>
      <p className="desc">Before arming, confirm in-game:</p>
      <ul className="arm-checklist">
        <li>Your character is in the expected starting area.</li>
        {selectedMode === 4 && <li>Atlas maps and scarabs are in the strategy's named supplies tab.</li>}
        {selectedMode === 4 && <li>The Atlas node and map device are available.</li>}
        {selectedMode === 5 && <li>Blighted maps are in the configured Blight supplies tab.</li>}
        {selectedMode === 6 && <li>Simulacrums are in the configured Simulacrum supplies tab.</li>}
        {selectedMode === 7 && <li>Carry 12 identified-or-identifiable Shaper Guardian maps plus Wisdom, Scour, Alchemy, and Chaos currency.</li>}
        {selectedMode === 0 && <li>Overlay mode will not automate Atlas, Blight, or Simulacrum runs.</li>}
      </ul>
      <div className="arm-hint">The bot only acts while PoE is the foreground window{status?.foreground ? "" : " — PoE is not focused right now"}.</div>
      {result && <div className="v">{result}</div>}
      <div className="wizard-arm-actions">
        <button type="button" className="btn-primary" disabled={busy} onClick={arm}>Arm &amp; go to dashboard</button>
        <button type="button" className="btn-secondary" onClick={onFinish}>Go to dashboard without arming</button>
      </div>
    </>
  );
}
