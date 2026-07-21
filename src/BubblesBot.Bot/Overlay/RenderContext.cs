using BubblesBot.Bot.Input;
using BubblesBot.Bot.Modes;
using BubblesBot.Bot.Overlay.Navigation;
using BubblesBot.Core.Knowledge;
using BubblesBot.Core.Pathfinding;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Overlay;

/// <summary>
/// What the renderer needs each frame. Built fresh by BotApp every tick.
/// </summary>
public sealed record RenderContext(
    GameSnapshot? Snapshot,
    IInputRouter Input,
    LootMode Loot,
    string Status,
    BotEnable? Enable = null,
    LivePlayer? Live = null,
    EntityCache? Entities = null,
    PriceCatalog? Prices = null,
    // Active mode's overlay HUD lines (reveal %, census, decisions) — null when the mode
    // doesn't publish any.
    IReadOnlyList<string>? Hud = null,
    // Campaign-guidance snapshot (area id + targets + diagnostic) published by the guidance worker,
    // for the HUD banner. Null when guidance is off or unresolved.
    GuidanceSnapshot? Guidance = null,
    // Per-frame guidance routes (player → each target), re-walked from the live player this frame.
    IReadOnlyList<GuidanceRoute>? GuidanceRoutes = null,
    // Compact non-interactive notice when GitHub reports a newer stable release.
    string? UpdateWarning = null,
    // Draw compact HP bars above hostile monsters (see BotSettings.ShowEntityHpBars).
    bool HpBars = false,
    // Draw the cyan player blip at map-overlay center (see BotSettings.ShowMapPlayerBlip).
    bool PlayerBlip = false);
