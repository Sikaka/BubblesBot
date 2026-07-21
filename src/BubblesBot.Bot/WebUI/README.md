# BubblesBot Web UI

React + TypeScript + Vite single-page app served by the bot's embedded HTTP server on
`http://localhost:5666`.

## Layout

- `src/` — application source (this is what you edit)
- `wwwroot/` — **generated** Vite build output, committed so `clone → dotnet run` works
  without Node. The bot's csproj copies it next to the exe; `Web/StaticFiles.cs` serves it
  with an `index.html` fallback for client-side routes.

## Workflows

| Task | Command |
|---|---|
| Live development (hot reload against a running bot) | `npm run dev` → http://localhost:5173 — `/api` and `/ws` proxy to the bot on :5666 |
| Rebuild the committed bundle | `npm run build` (or `dotnet build -p:BuildWebUi=true` from the repo root) |
| Preview the production bundle against a running bot | `npm run build && npm run preview` |

`npm run build` runs `tsc -b` first — type errors fail the build.

## Conventions

- The settings form is generated from `GET /api/settings/schema`; adding a `[Setting]`
  property in `BotSettings` needs no UI change. New field *types* need a renderer in
  `src/components/schema/` and a case in `SchemaField.tsx`.
- Settings edits are draft-local until the Save bar applies them (whole-object PUT).
  Never fire writes per keystroke — a stale page snapshot can clobber concurrent changes
  (Insert-hotkey arming, character profile switches).
- Live status comes from the `/ws` WebSocket (10 Hz) via `state/statusStore.ts`
  (zustand); subscribe with selectors, keep derived work in `useMemo`.
- Rebuild + commit `wwwroot/` in the same change as UI-affecting source edits — the
  committed bundle IS the deployed UI.
