import type { Schema, Settings } from "./types";

export interface SettingsEnvelope {
  settings: Settings;
  version: number;
}

export interface FieldError {
  path: string;
  message: string;
}

export interface ControlResponse {
  status: string;
  warnings: string[];
  reasons: string[];
}

export interface BotMeta {
  botVersion: string;
  gameAttached: boolean;
  gameProcessId?: number;
  gateAvailable: boolean;
  gameState: string;
  foreground: boolean;
  armed: boolean;
  activeMode: number;
  character: string;
  profile: string;
  league: string;
}

export interface PatchOp {
  path: string[];
  value: unknown;
}

/** Typed HTTP failure carrying the parsed body (422 field errors, 409 fresh envelope, …). */
export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly body: unknown,
    message: string,
  ) {
    super(message);
  }
}

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, init);
  const text = await response.text();
  const body = text.length > 0 ? safeParse(text) : null;
  if (!response.ok) {
    throw new ApiError(response.status, body, `${response.status} ${response.statusText}`);
  }
  return body as T;
}

function safeParse(text: string): unknown {
  try { return JSON.parse(text); } catch { return text; }
}

const json = (body: unknown): RequestInit => ({
  method: "POST",
  headers: { "Content-Type": "application/json" },
  body: JSON.stringify(body),
});

export const fetchSchema = () => request<Schema>("/api/settings/schema");
export const fetchSettings = () => request<SettingsEnvelope>("/api/settings");
export const fetchMeta = () => request<BotMeta>("/api/meta");

/** Whole-object apply (wizard bulk save). Server validates; 422 carries {errors}. */
export const putSettings = (settings: Settings) =>
  request<SettingsEnvelope>("/api/settings", {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(settings),
  });

/**
 * Path-targeted apply. 409 (ApiError.body = fresh envelope) when expectedVersion is stale;
 * 422 (ApiError.body = {errors}) on validation failures.
 */
export const patchSettings = (ops: PatchOp[], expectedVersion?: number) =>
  request<SettingsEnvelope>("/api/settings", {
    method: "PATCH",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ expectedVersion, ops }),
  });

export interface RunReport {
  runId: string;
  sessionId: string;
  mode: number;
  modeName: string;
  strategyId: string;
  strategyName: string;
  profile: string;
  league: string;
  mapName: string;
  startedUtc: string;
  endedUtc: string;
  durationSec: number;
  result: string;
  stopReason: string;
  mapIndex: number;
  mapsCompleted: number;
  lootChaos: number;
  lootChaosCumulative: number;
  chaosPerHour: number;
  itemsPicked: number;
  deaths: number;
}

export interface RunsSummary {
  runs: number;
  mapsCompleted: number;
  totalChaos: number;
  chaosPerHour: number;
  deaths: number;
}

export const fetchRuns = (limit = 100) =>
  request<{ runs: RunReport[]; summary: RunsSummary }>(`/api/runs?limit=${limit}`);

export const controlArm = (mode?: number) => request<ControlResponse>("/api/control/arm", json(mode != null ? { mode } : {}));
export const controlDisarm = () => request<ControlResponse>("/api/control/disarm", json({}));
export const controlMode = (mode: number, force = false) => request<ControlResponse>("/api/control/mode", json({ mode, force }));
export const postIncident = (note: string) => request<null>("/api/incident", { method: "POST", body: note });
