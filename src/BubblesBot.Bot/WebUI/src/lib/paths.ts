// Schema fields carry a `path` array — ["loot", "minChaosValue"] — so nested settings are
// read/written generically. All writes are immutable: they clone along the path so React
// state updates behave.

import type { SchemaField, Settings } from "../api/types";

export function fieldPath(field: SchemaField): string[] {
  return Array.isArray(field.path) && field.path.length > 0 ? field.path : [field.name];
}

export function pathGet(obj: unknown, path: string[]): unknown {
  let current: unknown = obj;
  for (const segment of path) {
    if (current == null || typeof current !== "object") return undefined;
    current = (current as Record<string, unknown>)[segment];
  }
  return current;
}

/** Immutable set: returns a new root with objects cloned along the path. */
export function pathSet(obj: Settings, path: string[], value: unknown): Settings {
  if (path.length === 0) return obj;
  const root: Record<string, unknown> = { ...obj };
  let current = root;
  for (let i = 0; i < path.length - 1; i++) {
    const segment = path[i];
    const child = current[segment];
    current[segment] = child != null && typeof child === "object" ? { ...(child as object) } : {};
    current = current[segment] as Record<string, unknown>;
  }
  current[path[path.length - 1]] = value;
  return root;
}

export function sameValue(a: unknown, b: unknown): boolean {
  return JSON.stringify(a) === JSON.stringify(b);
}
