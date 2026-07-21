export type BotModeId = 0 | 4 | 5 | 6;

export interface BotModeDefinition {
  id: BotModeId;
  name: string;
  shortDescription: string;
  detail: string;
}

export const BOT_MODES: BotModeDefinition[] = [
  {
    id: 0,
    name: "Overlay",
    shortDescription: "Manual play",
    detail: "Read-only guidance, labels, diagnostics, and manual looting assistance.",
  },
  {
    id: 4,
    name: "Atlas",
    shortDescription: "Map farming",
    detail: "Run maps using an Atlas strategy for map choice, scarabs, mechanics, exploration, and completion.",
  },
  {
    id: 6,
    name: "Simulacrum",
    shortDescription: "Wave farming",
    detail: "Manage supplies, arena waves, between-wave storage, deaths, and re-entry.",
  },
  {
    id: 5,
    name: "Blight",
    shortDescription: "Blighted maps",
    detail: "Repeat Blighted maps with pump defense, tower, chest, cleanup, and supply policies.",
  },
];

export function modeDefinition(id: number | null | undefined): BotModeDefinition {
  return BOT_MODES.find((mode) => mode.id === id) ?? BOT_MODES[0];
}
