# BubblesBot — Contributor Guide

External memory-reading bot framework for **Path of Exile 1**. Long-term replacement for ExileCore as AutoExile's underlying framework.

## Read first
- [`README.md`](README.md) — what this is, why
- [`PLANNING.md`](PLANNING.md) — scope, tiers, phased roadmap
- [`RESEARCH.md`](RESEARCH.md) — technical decisions and rationale
- [`resources/README.md`](resources/README.md) — DLL snapshot + API reference workflow

## Non-negotiable rules

**PoE1, not PoE2.** Always. ExileCore is the PoE1 overlay framework. All mechanics, scarabs, atlas references are PoE1.

**Stay external, read-only.** Memory access via `OpenProcess` + `ReadProcessMemory`. **Never** inject into PoE.exe — no DLL injection, no function hooking, no packet manipulation. Input via `SendInput` only (no driver-level / hardware input in v1). Rationale in `RESEARCH.md`.

**Three-pillar layout.** The repo is split into exactly three projects, one per concern:
- `src/BubblesBot.Core` — memory plumbing, offset table, snapshot/cache layer. Pure read-side. No game-loop awareness.
- `src/BubblesBot.Bot` — tick loop, modes/systems, input (`SendInput`), overlay/HUD, settings, dashboard. Everything the user touches. Depends on Core through the ergonomic API only — never imports `KnownOffsets`.
- `src/BubblesBot.Research` — validation harness (Probe), future offset-discovery tools (Hunter). Dev-time only; never linked into the bot binary at runtime.

**Pillar discipline.** Reverse-engineering / auto-discovery code goes in `Research`, not `Bot`. The bot validates a handful of canary reads on startup and refuses to run if offsets are stale — it does NOT self-heal. When a patch breaks things, run `Research` to produce a new offset table, commit it, restart the bot.

**No scope creep.** No public plugin system, no GGPK parsing, no hardware input layer, no PoE2 support. Explicit non-goals are listed in `PLANNING.md` — keep them non-goals unless we agree to revisit.

**No backwards-compatibility shims pre-v1.** The facade API will change shape as we port AutoExile against it. When something is wrong, change it — don't add migration layers. Nothing depends on us yet.

## Quick reference

- ExileCore public API surface: `resources/ExileCore-API-Reference.md` (searchable, ~300KB)
- Memory layouts / struct offsets: decompile `resources/dlls/GameOffsets.dll` with dnSpy/ILSpy — the markdown reference doesn't expose private memory layouts
- Live game state for validation: POEMCP at `http://localhost:5999` (see AutoExile's `CLAUDE.md` for endpoints)

## Code map

- `src/BubblesBot.Core/`
  - `MemoryReader.cs`, `ProcessHandle.cs`, `Native/` — Win32 + typed reads.
  - `Game/KnownOffsets.cs` — single source of truth for all PoE memory offsets. **Bot code never imports this.**
  - `Game/*Reader.cs`, `GameStructs.cs`, `EntityComponents.cs` — raw read helpers.
  - `Snapshot/` — the ergonomic API.
    - `GameSnapshot` (built per tick), `PlayerView`, `GroundLabelView`, `LivePlayer` (per-render fresh read).
    - `ElementGeometry` (parent-walk rect math), `WindowInfo`, `CanaryCheck`.
    - `NavGrid` (lazy terrain view, walkable + targeting layers), `MapView`, `CameraView` (matrix-projection world→screen, validated ±2 px).
    - `EntityCache` (cross-tick frozen-field cache + Hot/Warm/Cold tier scheduling).
    - `BuffsView`, `SkillBarView` — buff names + skill-bar slot ids.
    - `TileMapView` (static-per-area landmark grid: every terrain tile's detail name + path → grid positions; cached forever per AreaHash; ~20 ms one-time load). Lookups: `Find`, `FindNearest`, `FindNearestLandmark(LandmarkCatalog.Kind, ...)`, `FindByPathPrefix`.
    - `Game/LandmarkCatalog` — curated detail-name → friendly category map (Waypoint, AreaTransition, BossArena, Mechanic, …). `Register(name, kind, label)` to add per-mode entries.
    - `UiPattern` / `UiPatternMatcher` / `UiTreeNavigator` / `UiPatterns` / `UiIndexPaths` — UI panel discovery. Patterns describe shape; matcher BFS-walks UIRoot returning ranked index paths; runtime navigates committed paths via cheap pointer reads. `MapDeviceView.FromUiTree(reader, uiRoot)` is the first consumer.
  - `Pathfinding/` — `GridConstants` (10.88 world/grid), `ICellReader` + `TerrainCellReader`, `AStar` (8-neighbor + optional gap-blink expansion), `PathSmoother` (LOS simplification, preserves Blink anchors), `MapProjection`. `PathCell` carries `StepAction.Walk|Blink`.
  - `Knowledge/` — `PriceCatalog` (poe.ninja fetch + 6 h disk cache), `LootFilter` (chaos-value threshold + always-take allowlist).
- `src/BubblesBot.Bot/Input/`
  - `IInputRouter` + `InputRouter` — single front door for game input. One-in-flight click gate, per-vk held-key dictionary with idle-TTL + max-duration release. Auto-release on area change / foreground loss / Dispose. `MoveIntent`, `ClickIntent`, `HoldBudget`, `IInputHandle`.
  - `SendInputNative` — Win32 SendInput. Routes VKs 0x01-0x06 (mouse buttons including XBUTTON1/2) through MOUSEINPUT events; everything else through KEYBDINPUT.
- `src/BubblesBot.Bot/Systems/`
  - `MovementSystem` — holds the user's Walk-tagged skill key + retargets cursor; halt-on-self-hover stop trick.
  - `CombatSystem` — `Cast(slot, aim)` / `HoldChannel(slot, aim, predicate)`. Slots are passed by reference (variable-length profile, no integer indexing).
  - `Aim` — discriminated record: `AtSelf / AtGrid / AtEntity / AtClosestEnemy / AtAoeCluster`.
  - `SkillBook` — per-slot charge + last-cast tracker. `IsReady`, `PickReady`, `MillisUntilCharges`, `MarkCast`. v1 client-side simulation; replaced by validated `ActorSkillCooldown` reads later without API change.
  - `InteractSystem` — generalized "click an entity label and verify."
- `src/BubblesBot.Bot/Behaviors/`
  - `IBehavior`, `BehaviorContext`, `BehaviorStatus`. Composers: `Sequence / Selector / Parallel / If / Cooldown / Until / RetryWith / Invert`. Leaves: `Action / Condition`.
  - `Movement/`: `WalkTo`, `StopMoving`, `FollowPath` (gap-aware: dispatches `Blink` steps via `SkillBook.PickReady(GapCrossers)` + `TapKey`).
  - `Combat/`: `Cast`, `Channel`, `MaintainBuff` — all take a `Func<ctx, SkillSlot?>` slot picker.
  - `Loot/`: `LootClosestVisible` (3-attempt blacklist).
  - `TreeSnapshotVisitor` — flattens tree state for the dashboard.
- `src/BubblesBot.Bot/Modes/`
  - `IBotMode` plus four production modes: Overlay/manual (`0`), map farming (`4`), Blight (`5`), and Simulacrum (`6`). Obsolete waypoint/skill/combat/mapping test modes were removed from production dispatch.
  - `MapRunMode` owns the cross-area map lifecycle; `PushCombatMode` owns shared in-map exploration, dense-pack combat, loot detours, mechanics, and completion telemetry.
  - `BlightMode` owns the accepted repeat Blight-ravaged loop, including `Dump`/`Supplies`, device/portal flow, pump/towers, cleanup, rewards, and exit.
  - `SimulacrumRunMode` owns supply/device/death/re-entry states while `SimulacrumMode` owns arena waves, combat, loot, and between-wave storage.
- `src/BubblesBot.Bot/Settings/`
  - `BotSettings` — annotated properties; `[Setting]` + `[SettingRange]` / `[SettingKeycode]` / `[SettingOptions]` / `[SettingSkills]`.
  - `SkillProfile` + `SkillSlot` — variable-length skill bindings. `Role` enum (Disabled/Walk/Dash/Attack/SelfBuff/Channel/Aura), `CanCrossGaps` toggle, charge model.
  - `SettingsStore` — `%APPDATA%/BubblesBot/{config|profiles/<character>}.json`, debounced save, `RebindPath` for profile switch.
  - `ProfileStore` — auto-loads profile on character-name change.
- `src/BubblesBot.Bot/Overlay/` — `OverlayWindow` (per-pixel-alpha layered window via `UpdateLayeredWindow`), `OverlayRenderer` (status panel + map-mode terrain/entities/path), `TerrainBitmap`.
- `src/BubblesBot.Bot/Web/` — `WebServer` (HttpListener + WebSocket on :5666). Reflection-driven settings schema; live status JSON includes mode, behavior tree state, profile name.
- `src/BubblesBot.Bot/`
  - `Program.cs`, `Bootstrap.cs` (AOB scan with `--hp` value-scan fallback), `BotApp.cs` (tick loop + mode dispatch), `BotEnable.cs` (Insert hotkey, foreground gate, mode-aware status label).
- `src/BubblesBot.Research/`
  - `Validation/Tests/*.cs` — POEMCP-driven offset validation harness. Run after every PoE patch.
  - `Validation/OffsetSweep.cs` + `OffsetProbeCatalog.cs` — single-command offset sweep (`--sweep-offsets`). Reads probe registry, validates each via POEMCP, proposes drift fixes. Per-patch playbook: `resources/offset-validation-playbook.md`.
  - `--discover-field-aob` — lists candidate AOB patterns for self-healing field offsets.

## Tick model

Two cadences:
- **Render rate (144 Hz target)** — `BotApp.Tick`: read `LivePlayer`, drive `InputRouter.Tick`, render overlay. Lightweight per-frame work.
- **World rate (30 Hz)** — fresh `GameSnapshot`, refresh `EntityCache`, dispatch the active mode, recompute debug path. Snapshot accessors are lazy + cached for the snapshot's lifetime; cross-tick caches live in `EntityCache`.

The active mode comes from `BotSettings.ActiveMode`. `IBotMode.Tick(snapshot, IInputRouter)` is the entry point; modes are re-entrant — they re-evaluate from current state every tick.

## Input model

Every input passes through `IInputRouter`:
- `Click(absX, absY, intent, expect)` / `TapKey(vk, intent, expect)` — gated, one in flight, post-condition or timeout.
- `BeginHoldKey(vk, budget)` — returns an `IInputHandle`; refresh each tick to keep alive, auto-release on TTL/area-change/foreground-loss.
- `HoverAt(x, y)` — cursor-only, unthrottled (`SetCursorPos` is microsecond-cheap; combat hovers several times/tick). When multiple callers want the cursor in one tick, arbitrate via `CursorArbiter`.

Holds coexist with the click gate. Mouse-button VKs (0x01-0x06) are dispatched as `MOUSEINPUT` events.

## Skills + navigation

Skills are configured as a variable-length `SkillProfile`. Each `SkillSlot` has Vk, Role, optional `CanCrossGaps`, charge count, recharge interval, range. The Walk slot drives `MovementSystem`'s held key; gap-crossing dashes drive `FollowPath`'s blink execution.

A* takes an optional `GapPlan` + targeting reader. When enabled, expansion from cells bordering pf=0 scans across the gap on the targeting layer for landing cells; landings are added as candidate edges with a flat-penalty cost. Caller computes the penalty from current charge readiness — high penalty when no charges ready (planner avoids gaps), low when charges available. `StepAction.Blink` cells are preserved by `PathSmoother` so the executor sees them. `FollowPath` taps the first ready gap-crossing dash via `SkillBook.PickReady(profile.GapCrossers)` and aims the cursor at the landing.
