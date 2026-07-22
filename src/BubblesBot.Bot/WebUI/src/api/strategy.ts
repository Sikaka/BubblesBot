// Typed mirror of the FarmingStrategy document (src/BubblesBot.Bot/Strategies/FarmingStrategy.cs).
// The strategy shape is a small, stable, polymorphic contract, so a hand-written typed mirror
// is clearer than forcing it through the reflection schema. Unknown mechanic types / fields are
// rejected server-side, so anything that round-trips here is already valid shape.

export type MechanicType = "shrines" | "eldritchAltars" | "ritual" | "memoryTears" | "delirium" | "ultimatum" | "strongboxes";
export type AltarChoicePolicy = "skip" | "top" | "bottom" | "smart";
export type RitualChainOrdering = "nearestFirst" | "cloisterCorpses";
export type MapSource = "atlasStorage" | "playerInventory" | "stashTab";
export type MultiAreaPolicy = "exhaustAllZones";
export type CampaignMode = "none" | "guardianRotation";
export type MapRollingMode = "none" | "scoured" | "rare" | "rareCorrupted";

export interface MechanicBlockBase {
  type: MechanicType;
  enabled: boolean;
  sweepBias: number;
}

export interface ShrinesBlock extends MechanicBlockBase { type: "shrines" }
export interface MemoryTearsBlock extends MechanicBlockBase { type: "memoryTears" }
export interface StrongboxesBlock extends MechanicBlockBase { type: "strongboxes" }

export interface DeliriumBlock extends MechanicBlockBase {
  type: "delirium";
  initialFogLeadGrid: number;
  maxForwardGridPerSecond: number;
  maximumPackDwellSeconds: number;
  endButtonTimeoutSeconds: number;
  minimumRewardWaitSeconds: number;
  rewardQuietSeconds: number;
  maximumRewardWaitSeconds: number;
}

export interface UltimatumBlock extends MechanicBlockBase {
  type: "ultimatum";
  exitAfter: boolean;
  doSurvive: boolean;
  doKillEnemies: boolean;
  doDefendAltar: boolean;
  doStandInCircles: boolean;
}

export interface EldritchAltarsBlock extends MechanicBlockBase {
  type: "eldritchAltars";
  policy: AltarChoicePolicy;
  deferChoicesUntilBossDead: boolean;
  weightOverrides: Record<string, number>;
}

export interface RitualShopBlock {
  enabled: boolean;
  rerollThresholdChaos: number;
  finalBuyMinChaos: number;
  maxRerolls: number;
}

export interface RitualBlock extends MechanicBlockBase {
  type: "ritual";
  deferUntilMapSweep: boolean;
  chainOrdering: RitualChainOrdering;
  corpseMonsterPathFragment: string;
  corpseRadiusGrid: number;
  densityWeight: number;
  shop: RitualShopBlock;
}

export type MechanicBlock =
  | ShrinesBlock | MemoryTearsBlock | DeliriumBlock | UltimatumBlock | StrongboxesBlock | EldritchAltarsBlock | RitualBlock;

export interface ScarabLine {
  pathFragment: string;
  displayName: string;
  countPerMap: number;
}

export interface CurrencyReserve {
  item: string;
  minCount: number;
  policy: "retainFullestStack";
}

export interface FarmingStrategy {
  schemaVersion: number;
  identity: {
    id: string;
    name: string;
    description: string;
    author: string;
    gameVersion: string;
    createdUtc: string;
    modifiedUtc: string;
  };
  supply: {
    suppliesTabName: string;
    dumpTabName: string;
    map: { source: MapSource; targetMapName: string; restockFromStash?: boolean; carriedMapBuffer?: number };
    scarabs: ScarabLine[];
    currencyReserves: CurrencyReserve[];
  };
  mapPrep: { atlasNodeName: string; rolling: { mode: MapRollingMode; rejectedStatIds: number[]; maxAttempts: number } };
  mechanics: MechanicBlock[];
  loot: { backtrackMinChaosOverride: number | null; depositAfterEachMap: boolean };
  completion: { requireBossKill: boolean; multiArea: MultiAreaPolicy; targetMaps: number; explorationDonePercent: number };
  campaign: { mode: CampaignMode };
  limits: { maxZoneMinutes: number | null; maxMechanicStallsPerMap: number };
}

export interface StrategySummary {
  id: string;
  name: string;
  description: string;
  author: string;
  gameVersion: string;
  updatedUtc: string;
  mode: number;
  active: boolean;
  valid: boolean;
  summary: { mapName: string; targetMaps: number; mechanics: string[] };
}

export interface StrategyListResponse {
  strategies: StrategySummary[];
  activeId: string | null;
  loadErrors: string[];
}

export interface StrategyTemplate {
  templateId: string;
  name: string;
  description: string;
  mode: number;
}

export interface StrategySaveResponse {
  strategy: FarmingStrategy;
  errors: string[];
  warnings: string[];
}

export const MECHANIC_LABELS: Record<MechanicType, string> = {
  shrines: "Shrines",
  eldritchAltars: "Eldritch altars",
  ritual: "Ritual",
  memoryTears: "Memory tears",
  delirium: "Delirium",
  ultimatum: "Ultimatum",
  strongboxes: "Strongboxes",
};
