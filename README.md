# BubblesBot

External memory-reading bot framework for **Path of Exile 1**, intended as a long-term replacement for the ExileCore dependency currently used by AutoExile.

## Why this project exists

AutoExile is a plugin loaded by ExileCore, which is closed-source and ships only as compiled DLLs. That model has three pain points we want to leave behind:

1. **Debug ergonomics are bad.** Plugins build to DLLs, are loaded by `ExileCore.exe` at runtime, and lock the file on disk. There is no F5-launch, no edit-and-continue, no source stepping into framework code, and a kill/build/deploy/restart cycle for every change. Stepping into ExileCore lands in dnSpy decompilation, not source.
2. **The API surfaces the memory layout, not the use case.** Real bot code looks like `gc.IngameState.IngameUi.StashElement.VisibleStash.InventoryUiElement.GetChildFromIndices(0,1,2)`. We want `bot.UI.Stash.VisibleItems`.
3. **No control over instrumentation.** We cannot extend ExileCore's tick loop, add tracing/recording at the framework layer, or build first-class replay tooling — only patch over the top.

## What this is not

- Not an in-process injection / function-hooking / packet-crafting tool. We stay external and read-only, same posture as ExileCore. See `RESEARCH.md` for the rationale.
- Not a public plugin host. AutoExile's needs drive the API; we are not building a framework for third-party plugins. This collapses scope significantly.

## Top-level architecture

External `.exe` that:
1. Attaches to `PoE.exe` via `OpenProcess` + `NtReadVirtualMemory` (no injection, no hooks).
2. Reads PoE structs directly using a maintained offset table (see `resources/community-offsets.md` and `src/BubblesBot.Core/Game/KnownOffsets.cs`).
3. Exposes a curated, use-case-shaped API to bot logic — not a 1:1 mirror of ExileCore.
4. Drives input via `SendInput`.
5. Owns its own tick loop, render overlay, and observability (web dashboard, tick recorder, replay harness).
