import { create } from "zustand";
import type { FarmingStrategy } from "../api/strategy";
import type { Settings } from "../api/types";

/**
 * Setup-wizard draft. Nothing is written to the bot until the Review step: build-scope settings
 * accumulate in `settingsDraft` (applied as a PATCH), and the farming strategy accumulates in
 * `strategyDraft` (created/saved + activated). Autosaved to localStorage so a refresh mid-setup
 * doesn't lose progress.
 */
export interface WizardState {
  step: number;
  /** Full settings document being edited (build scope: skills, flasks, combat, movement). */
  settingsDraft: Settings | null;
  /** Settings version captured at load, for the PATCH concurrency check. */
  settingsVersion: number;
  /** The strategy being built from a template. Null until an archetype is chosen. */
  strategyDraft: FarmingStrategy | null;
  /** Top-level runtime mode: Overlay=0, Atlas=4, Blight=5, Simulacrum=6, Guardian Rota=7. */
  selectedMode: number | null;
}

// v2 deliberately drops old `legacyMode` drafts. Those drafts could survive an abandoned
// setup and make a later Blight setup silently reactivate Simulacrum.
const STORAGE_KEY = "bubblesbot.wizard.draft.v2";
const LEGACY_STORAGE_KEY = "bubblesbot.wizard.draft";

function load(): Partial<WizardState> {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    return raw ? (JSON.parse(raw) as Partial<WizardState>) : {};
  } catch {
    return {};
  }
}

export const useWizardStore = create<WizardState>(() => ({
  step: 0,
  settingsDraft: null,
  settingsVersion: 0,
  strategyDraft: null,
  selectedMode: null,
  ...load(),
}));

useWizardStore.subscribe((state) => {
  try {
    // Persist only the serializable draft (not transient step chrome would also be fine).
    localStorage.setItem(STORAGE_KEY, JSON.stringify({
      step: state.step,
      settingsDraft: state.settingsDraft,
      settingsVersion: state.settingsVersion,
      strategyDraft: state.strategyDraft,
      selectedMode: state.selectedMode,
    }));
  } catch { /* storage full / disabled — draft just won't persist */ }
});

export function clearWizardDraft(): void {
  try {
    localStorage.removeItem(STORAGE_KEY);
    localStorage.removeItem(LEGACY_STORAGE_KEY);
  } catch { /* ignore */ }
  useWizardStore.setState({ step: 0, settingsDraft: null, settingsVersion: 0, strategyDraft: null, selectedMode: null });
}
