// Shapes served by the bot's embedded HTTP server. Settings are intentionally loose —
// the schema endpoint drives rendering, and unknown fields must round-trip untouched.

export type FieldType =
  | "modtable" | "flasks" | "skills" | "stringlist" | "options"
  | "bool" | "keycode" | "int" | "float" | "string" | "unknown";

export interface SchemaField {
  name: string;
  path: string[];
  category: string;
  displayName: string;
  description: string;
  type: FieldType;
  min?: number | null;
  max?: number | null;
  step?: number | null;
  placeholder?: string | null;
  options?: { label: string; value: number }[] | null;
  mods?: { id: string; name: string; defaultDanger: number }[] | null;
  tiers?: { label: string; value: number }[] | null;
}

export interface Schema { fields: SchemaField[] }

/** Whole BotSettings document. Edited via schema paths; unknown members must survive. */
export type Settings = Record<string, unknown>;

export interface SkillSlot {
  name: string;
  vk: number;
  role: number;
  canCrossGaps: boolean;
  minCastIntervalMs: number;
  maxRangeGrid: number;
  chargeCount: number;
  chargeRechargeMs: number;
  gemId?: number;
  [extra: string]: unknown;   // server-side timing fields round-trip untouched
}

export interface FlaskSlot {
  name: string;
  vk: number;
  trigger: number;
  hpThreshold: number;
  manaThreshold: number;
  intervalMs: number;
  buffName: string;
  cooldownMs: number;
  [extra: string]: unknown;
}

export interface SlotProfile<T> { slots: T[]; [extra: string]: unknown }

export interface LiveSkill {
  barSlot: number;
  gemId: number;
  name: string;
  isReady: boolean;
  maxUses: number;
}

export interface TreeNode { depth: number; name: string; status: string }

export interface BotEvent {
  seq: number;
  t: string;
  category: string;
  eventType: string;
  severity: string;
  message: string;
}

export interface LootEntry {
  at: string;
  name: string;
  category: string;
  stackCount: number;
  chaosValue: number;
  maxChaosValue: number;
  reason: string;
}

export interface LootLedger {
  pickups: number;
  totalChaos: number;
  maxPlausibleChaos: number;
  chaosPerHour: number;
  byCategory: Record<string, number>;
  recent: LootEntry[];
}

export interface LoopTelemetry {
  step: string;
  phase: string;
  lifecyclePhase: string;
  preset: string;
  resourcePolicy: string;
  mapsCompleted: number;
  targetMaps: number;
  itemsStashed: number;
  portalScrolls: number;
  stopped: boolean;
  stopReason: string;
  [extra: string]: unknown;
}

export interface RunTimer {
  running: boolean;
  expired: boolean;
  elapsedSeconds: number;
  remainingSeconds?: number | null;
  limitMinutes: number;
}

export interface UpdateInfo {
  currentVersion: string;
  latestVersion?: string | null;
  updateAvailable: boolean;
  releaseUrl?: string | null;
  state: "checking" | "current" | "available" | "no-release" | "unavailable";
  checkedAtUtc?: string | null;
}

/** The 10 Hz status payload. Only the fields the UI consumes are typed. */
export interface BotStatus {
  update?: UpdateInfo;
  connected: boolean;
  stateLabel: string;
  foreground?: boolean;
  gameState?: string;
  armed?: boolean;
  activeMode?: number;
  shouldAct?: boolean;
  shouldLoot?: boolean;
  lootKeyHeld?: boolean;
  playerHp?: number;
  playerHpMax?: number;
  playerGridX?: number;
  playerGridY?: number;
  areaHash?: number;
  labelsVisible?: number;
  openPanels?: string[];
  worldBlocked?: boolean;
  inputState?: string;
  mode?: string;
  modeDecision?: string;
  lootDecision?: string;
  runId?: string | null;
  profile?: string;
  character?: string;
  league?: string;
  runTimer?: RunTimer;
  loop?: LoopTelemetry | null;
  lootLedger?: LootLedger;
  liveSkills?: LiveSkill[];
  events?: BotEvent[];
  tree?: TreeNode[];
  [extra: string]: unknown;
}
