import { useState } from "react";
import { ApiError, controlArm, controlDisarm, controlMode, type ControlResponse } from "../../api/client";
import { BOT_MODES, type BotModeId } from "../../lib/modes";
import { reflectActiveMode, refreshStatusNow, useStatusStore } from "../../state/statusStore";

/**
 * Arm/disarm + mode switch through the control API. Arming here is the same persisted flag
 * as the in-game Insert hotkey; the bot still only ACTS while PoE is the foreground window —
 * surfaced as a warning rather than a failure.
 */
export function ArmControl() {
  const status = useStatusStore((s) => s.status);
  const [messages, setMessages] = useState<{ text: string; bad: boolean }[]>([]);
  const [busy, setBusy] = useState(false);

  const armed = !!status?.armed;
  const activeMode = status ? Number(status.activeMode ?? 0) : null;

  const run = async (action: () => Promise<ControlResponse>, onSuccess?: () => void) => {
    setBusy(true);
    setMessages([]);
    try {
      const result = await action();
      setMessages(result.warnings.map((w) => ({ text: w, bad: false })));
      onSuccess?.();
    } catch (e) {
      if (e instanceof ApiError && e.body && typeof e.body === "object") {
        const body = e.body as ControlResponse;
        setMessages((body.reasons ?? [String(e)]).map((r) => ({ text: r, bad: true })));
      } else {
        setMessages([{ text: String(e), bad: true }]);
      }
    } finally {
      setBusy(false);
    }
  };

  const switchMode = (mode: BotModeId) => run(
    () => controlMode(mode),
    () => {
      reflectActiveMode(mode);
      void refreshStatusNow();
    },
  );

  return (
    <section className="card mode-control-card">
      <div className="mode-control-head">
        <div>
          <span className="setup-kicker">Run mode</span>
          <h2>Choose what BubblesBot should do</h2>
        </div>
        <span className={`mode-lock ${armed ? "warn" : ""}`}>{armed ? "Disarm to change mode" : "Mode can be changed"}</span>
      </div>
      <div className="mode-grid">
        {BOT_MODES.map((mode) => (
          <button key={mode.id} type="button"
            className={`mode-card ${activeMode === mode.id ? "active" : ""}`}
            disabled={busy || armed || !status || activeMode === mode.id}
            onClick={() => switchMode(mode.id)}>
            <span className="mode-card-check">{activeMode === mode.id ? "Active" : "Select"}</span>
            <strong>{mode.name}</strong>
            <span>{mode.shortDescription}</span>
            <small>{mode.detail}</small>
          </button>
        ))}
      </div>
      <div className="arm-row mode-arm-row">
        <button
          type="button"
          className={armed ? "btn-secondary arm-btn" : "btn-primary arm-btn"}
          disabled={busy || !status}
          onClick={() => run(armed ? controlDisarm : () => controlArm())}
        >
          {armed ? "Disarm" : "Arm"}
        </button>
        <span className={`v ${armed ? (status?.shouldAct ? "good" : "warn") : ""}`}>
          {status?.stateLabel ?? "—"}
        </span>
      </div>
      {!status?.foreground && armed && (
        <div className="arm-hint">PoE is not focused — the bot only acts while PoE is the foreground window.</div>
      )}
      {messages.map((message, i) => (
        <div key={i} className={`arm-hint ${message.bad ? "v bad" : ""}`}>{message.text}</div>
      ))}
    </section>
  );
}
