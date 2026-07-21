import { create } from "zustand";
import type { BotStatus } from "../api/types";
import { modeDefinition, type BotModeId } from "../lib/modes";

export type ConnectionState = "connecting" | "live" | "polling" | "disconnected";

interface StatusStore {
  connection: ConnectionState;
  status: BotStatus | null;
}

export const useStatusStore = create<StatusStore>(() => ({
  connection: "connecting",
  status: null,
}));

let started = false;

/**
 * Live status feed. Prefers the 10 Hz WebSocket; if the socket can't be established (e.g. the
 * bot is on its raw-TCP fallback transport, which has no /ws), it degrades to polling
 * /api/status. Call once at app start.
 */
export function startStatusSocket(): void {
  if (started) return;
  started = true;
  connectSocket(0);
}

function connectSocket(failures: number): void {
  // After two failed socket attempts (no /ws), give up on WS and poll instead.
  if (failures >= 2) {
    startPolling();
    return;
  }

  const proto = location.protocol === "https:" ? "wss:" : "ws:";
  let opened = false;
  const ws = new WebSocket(`${proto}//${location.host}/ws`);
  useStatusStore.setState({ connection: "connecting" });

  ws.onopen = () => { opened = true; useStatusStore.setState({ connection: "live" }); };
  ws.onmessage = (e) => {
    try { useStatusStore.setState({ status: JSON.parse(e.data) as BotStatus }); }
    catch { /* skip malformed frame */ }
  };
  ws.onerror = () => ws.close();
  ws.onclose = () => {
    if (opened) {
      // A previously-working socket dropped — reconnect fresh (failure count resets).
      useStatusStore.setState({ connection: "disconnected" });
      setTimeout(() => connectSocket(0), 1000);
    } else {
      // Never opened — likely no WS endpoint. Count toward the polling cutover.
      setTimeout(() => connectSocket(failures + 1), 500);
    }
  };
}

let polling = false;

/** Reflect a successful control response immediately; the live feed replaces it next tick. */
export function reflectActiveMode(mode: BotModeId): void {
  useStatusStore.setState((state) => ({
    status: state.status ? { ...state.status, activeMode: mode, mode: modeDefinition(mode).name } : state.status,
  }));
}

/** One-shot refresh used after setup/control mutations instead of waiting for the poll interval. */
export async function refreshStatusNow(): Promise<void> {
  try {
    const response = await fetch("/api/status");
    if (response.ok) {
      useStatusStore.setState({ status: (await response.json()) as BotStatus });
    }
  } catch { /* the connection state/poller owns retry reporting */ }
}

function startPolling(): void {
  if (polling) return;
  polling = true;
  useStatusStore.setState({ connection: "polling" });

  const tick = async () => {
    const before = useStatusStore.getState().status;
    await refreshStatusNow();
    useStatusStore.setState({ connection: useStatusStore.getState().status === before ? "disconnected" : "polling" });
  };

  void tick();
  setInterval(tick, 500);
}
