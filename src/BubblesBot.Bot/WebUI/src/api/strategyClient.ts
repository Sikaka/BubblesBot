import { ApiError } from "./client";
import type {
  FarmingStrategy, StrategyListResponse, StrategySaveResponse, StrategyTemplate,
} from "./strategy";

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, init);
  const text = await response.text();
  const body = text.length > 0 ? safeParse(text) : null;
  if (!response.ok) throw new ApiError(response.status, body, `${response.status} ${response.statusText}`);
  return body as T;
}

function safeParse(text: string): unknown {
  try { return JSON.parse(text); } catch { return text; }
}

export const listStrategies = () => request<StrategyListResponse>("/api/strategies");
export const getStrategy = (id: string) => request<FarmingStrategy>(`/api/strategies/${encodeURIComponent(id)}`);
export const listTemplates = () => request<{ templates: StrategyTemplate[] }>("/api/strategies/templates");

export const createStrategy = (body: { name?: string; fromTemplate?: string; fromStrategy?: string }) =>
  request<FarmingStrategy>("/api/strategies", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });

export const saveStrategy = (id: string, doc: FarmingStrategy) =>
  request<StrategySaveResponse>(`/api/strategies/${encodeURIComponent(id)}`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(doc),
  });

export const deleteStrategy = (id: string) =>
  request<null>(`/api/strategies/${encodeURIComponent(id)}`, { method: "DELETE" });

export const activateStrategy = (id: string) =>
  request<{ activeId: string; warnings: string[] }>(`/api/strategies/${encodeURIComponent(id)}/activate`, {
    method: "POST",
  });

export const importStrategy = (json: string) =>
  request<FarmingStrategy>("/api/strategies/import", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: json,
  });

/** Trigger a browser download of the strategy's export file. */
export function exportStrategyUrl(id: string): string {
  return `/api/strategies/${encodeURIComponent(id)}/export`;
}
