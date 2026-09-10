# MHW Fight Logs — web viewer

Browser viewer for the fight logs written by the [MHW DPS Meter](../README.md) plugin. Static site, no backend: it reads the plugin's `logs/` folder straight from your disk.

Stack: **SolidJS 2** (release candidate), **TanStack Table** (`@tanstack/table-core` v9) and **TanStack Charts** (`@tanstack/charts`), **daisyUI 5** on Tailwind 4, Vite 8, Vitest.

## Run

```bash
cd web
pnpm install
pnpm dev          # http://localhost:5173
pnpm test         # analysis + table binding tests against the sample logs
pnpm build        # type-check + static build in dist/
```

## Loading logs

- **Open logs folder** (Chromium browsers): picks `nativePC/plugins/CSharp/MhwDpsMeter/logs/` once with the File System Access API. The handle is remembered in IndexedDB; on the next visit one click reconnects it. Files are read on demand and never uploaded.
- **Open files / drag and drop**: any browser. Drop the whole folder's JSON files (with or without `index.json`).
- **Sample**: the bundled anonymised logs in `public/sample/`.
- **`?logs=<url>`**: any served folder that contains an `index.json`, for hosting your own logs.

## What it shows

- **Hunt list**: every quest and time trial from `index.json`, filterable by text and kind, sortable by date, result, duration, damage.
- **Overview**: party table with damage/DPS/share, cumulative damage per hunter with monster death and enrage markers, rolling DPS with a selectable window.
- **Moves**: per-move breakdown per hunter (damage, share, hits, crit rate, avg, max, tenderized) with a bar chart. Your rows are exact hits; teammates' rows (plugin 0.4.0+) are *estimated* from award-table increments credited to the move they were performing and are labelled as such. Toggle to hide `Common::` actions, which are hits that registered after the move animation ended.
- **Parts**: damage by monster part for the selected monster, with hunters ranked on each part. Part tags exist only on your exact hits; teammate damage stays under Unknown part (see `docs/part-damage.md`).
- **Layout**: drag the separator between the hunt list and the detail pane; the width is remembered per browser.
- **Monsters**: HP lost per large monster and how much of it was yours.
- **Timeline**: enrage, unenrage, death, flinch, weapon swaps, hunters joining/leaving.
- **Time trials**: personal bests per weapon and window (same rule as the plugin's F9 panel), every run, and a two-run comparison with overlaid damage curves and top moves.

Exact per-hit data (crit, tenderize) exists only for the local hunter: the game runs its deal-damage function only for hits simulated on your client. Teammates get their award total, the 2-second damage curve, and estimated per-move rows when the plugin could match their hunter entity to their slot.

## Move names

`src/moves/names.ts` maps the game's internal action names (`WP_02::RANBU`) to display names per weapon type. Only verified moves are listed; unknown ones are prettified (`Kijin Sliding On`). Add to the table as you confirm moves in your own logs; the raw name is always shown under the display name.

## Why not the official Solid adapters

`@tanstack/solid-table` and `@tanstack/charts/solid` target Solid 1: they import `solid-js/web`, `batch`, `createComputed`, `observable` and `onMount`, all removed in Solid 2. This project drives the framework-agnostic cores instead:

- `src/lib/table.ts`: `constructTable` with table-core's store reactivity bindings; Solid signals own `data` and `sorting`, a memo pushes them into the table, derived memos read the row model back.
- `src/lib/Chart.tsx`: `mountChart` (the DOM host) driven by a two-phase `createEffect`.

Both are small and covered by `tests/table.test.ts`.

## Sample data

`public/sample/` holds five real hunts and one time trial with hunter names replaced by "Hunter A/B". Everything else (moves, hits, monsters, timings) is untouched, so the sample is representative of real files.
