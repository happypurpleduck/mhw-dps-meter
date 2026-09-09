# MHW DPS Meter

In-game damage overlay for Monster Hunter World: Iceborne. It is a [SharpPluginLoader](https://fexty12573.github.io/SharpPluginLoader/) C# plugin, so it draws on the game’s own swapchain and works under Proton on Linux. There is no external overlay process.

During a quest it shows each hunter’s **name**, **total damage**, **DPS**, and **% of party damage**. When the quest ends it writes a JSON fight log with every one of your hits, the monsters, and a timeline. In the **training area** it shows your own damage and DPS against the pole, with **F7** to reset and **F8** to run a fixed-length **time trial** with per-move results and personal bests.

## Requirements

- Monster Hunter World: Iceborne (Steam), game **15.23**. Address maps ship for builds **421810** (current Steam build) and **421631**. The build is the number in the game window title, `MONSTER HUNTER: WORLD(421810)`.
- [SharpPluginLoader](https://github.com/Fexty12573/SharpPluginLoader/releases) — **Windows** release (`winmm.dll`) for Windows, **Linux** release (`ucrtbase.dll`) for Proton/Wine.
- .NET Desktop Runtime 8.0 (**x64** on Windows; **inside the Proton prefix** on Linux — `protontricks 582010 dotnetdesktop8`)
- Optional: [Stracker’s Loader](https://www.nexusmods.com/monsterhunterworld/mods/1982) if you also use native `nativePC` mods. This overlay does **not** need it, and does **not** need CRCBypass.

## Windows install

1. Install [.NET 8.0 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0/runtime) — pick **.NET Desktop Runtime**, not just the .NET Runtime.

2. Download [SharpPluginLoader](https://github.com/Fexty12573/SharpPluginLoader/releases) **Windows** release (`SharpPluginLoader-<version>.zip`) and extract it into the game root (the folder containing `MonsterHunterWorld.exe`). You should have `winmm.dll` beside the exe and a `nativePC/plugins/CSharp/` folder. If the game closes immediately on launch without Steam, add `MonsterHunterWorld.exe` as a Non-Steam Game and launch through Steam (the Steam overlay changes `winmm.dll` load order).

3. Download `MhwDpsMeter-0.4.0.zip` from this repo’s [Releases](https://github.com/anomalyco/mhw-dps-meter/releases) (or build it — see below) and **extract it into the same game root**. It creates:

   ```text
   nativePC/plugins/CSharp/MhwDpsMeter/MhwDpsMeter.dll
   nativePC/plugins/CSharp/MhwDpsMeter/Addresses/MonsterHunterWorld.421810.map
   nativePC/plugins/CSharp/MhwDpsMeter/Addresses/MonsterHunterWorld.421631.map
   ```

   Copy **only** that folder — do not duplicate the DLL directly into `CSharp/` and do not put it under `CSharp/Loader/`.

4. Optional: extract Stracker’s Loader into the game root (`dinput8.dll`) if you use other `nativePC` mods.

5. Launch the game. A SharpPluginLoader console should appear. Press **F9** — you should see **DPS Meter** with a status line. The first line, `Plugin build: 0.4.0+<UTC build time>`, tells you which DLL is actually running; if it does not match the one you copied, quit and relaunch. `Game build:` should name the map matching your exe. Depart on a quest; overlay is top-right. **F10** toggles it.

If F9 does nothing, SPL is not injecting. Check that `winmm.dll` is beside `MonsterHunterWorld.exe`, that your antivirus has not quarantined it, and that `loader-config.json` next to the exe has `"logfile": true` to write loader logs.

## Linux / Proton install

Proton 11 (and CachyOS Proton) will not load the `msvcrt.dll` injector from SharpPluginLoader 0.0.9. Use a newer Linux build whose injector is `ucrtbase.dll`.

1. Install prefix dependencies:

   ```bash
   protontricks 582010 dotnetdesktop8 d3dcompiler_47
   ```

2. Download a **post-0.0.9** Linux package (GitHub Actions artifact `SharpPluginLoader-*-linux.zip` from [MSBuild on master](https://github.com/Fexty12573/SharpPluginLoader/actions/workflows/msbuild.yml)) and extract it into the game root. You must have `ucrtbase.dll` beside `MonsterHunterWorld.exe`.

3. Optional: extract Stracker’s Loader into that same folder (`dinput8.dll`).

4. Steam launch options (keep Wayland/capture vars if you use them):

   ```text
   WINEDLLOVERRIDES="ucrtbase=n,b;dinput8=n,b" %command%
   ```

   If a system `DOTNET_ROOT` breaks CLR load:

   ```text
   DOTNET_ROOT= WINEDLLOVERRIDES="ucrtbase=n,b;dinput8=n,b" %command%
   ```

5. Download `MhwDpsMeter-0.4.0.zip` and extract it into the game root (same layout as Windows):

   ```text
   nativePC/plugins/CSharp/MhwDpsMeter/MhwDpsMeter.dll
   nativePC/plugins/CSharp/MhwDpsMeter/Addresses/MonsterHunterWorld.421810.map
   nativePC/plugins/CSharp/MhwDpsMeter/Addresses/MonsterHunterWorld.421631.map
   ```

6. Launch the game. A SharpPluginLoader console should appear. Press **F9** — you should see **DPS Meter** with a status line. The first line, `Plugin build: 0.4.0+<UTC build time>`, tells you which DLL is actually running; if it does not match the one you copied, quit and relaunch. `Game build:` should name the map matching your exe. Depart on a quest; overlay is top-right. **F10** toggles it.

If F9 does nothing, SPL still is not injecting. Check that `ucrtbase.dll` exists in the game root and that launch options include `ucrtbase=n,b`. Loader logs go next to the exe when `loader-config.json` has `"logfile": true`.

SharpPluginLoader reloads a plugin when its DLL changes. Under Proton that watcher often misses the copy, so quit the game fully and relaunch after updating the plugin.

## Automated builds and releases

GitHub Actions builds and tests the mod and native GPUI app on Linux and Windows.
See the [release guide](docs/releases.md) for downloads, changesets, changelogs,
and the release PR workflow.

## Development setup

Install [mise](https://mise.jdx.dev/getting-started.html), then run from the repository root:

```bash
mise trust
mise install
mise run setup
mise tasks
```

`mise.toml` pins the .NET SDK, Rust, Node.js, pnpm, and Python. Keep the .NET
pin in sync with `global.json`, and the Rust and Node versions compatible with
`.github/workflows/ci.yml`. The root release workspace uses npm; both web apps
use pnpm and their own lockfiles.

```bash
mise run build:plugin
mise run test:plugin
mise run dev:web
mise run build:web
mise run test:web
mise run build:viewer
mise run test:release
mise run test              # all suites, including the native viewer
```

Use `mise exec -- <command>` for other commands, or activate mise in your shell
to use the pinned tools directly. Tasks also work from subdirectories.
Native viewer builds still need the [platform libraries](gpui-viewer/README.md#native).
The optional WebAssembly viewer needs the additional nightly Rust and
wasm-bindgen setup in the [viewer README](gpui-viewer/README.md).

## Build

Use the mise development setup above to install the .NET 8 SDK.

```bash
mise run build:plugin
# or: mise exec -- scripts/package.sh   # builds and zips dist/MhwDpsMeter-0.4.0.zip
```

Output:

```text
dist/Release/nativePC/plugins/CSharp/MhwDpsMeter/   # raw build
dist/MhwDpsMeter-0.4.0.zip                            # game-root zip (same DLL works on Windows + Proton)
```

Extract the zip into the game root (beside `MonsterHunterWorld.exe`). The plugin DLL is the same on both platforms — only the SharpPluginLoader injector differs (`winmm.dll` on Windows, `ucrtbase.dll` on Proton).

## Overlay

| Column | Meaning |
| --- | --- |
| Name | Party slot name (`*` = you) |
| Damage | Per-hunter quest-award total (the results-screen value). Solo/arena without that table falls back to your live hits or large-monster HP lost |
| DPS | `damage / hunt clock`. The clock starts when the quest enters the in-quest state (roughly when the loading screen ends) and is the same for every hunter; logs record it as `timerSource: "local"`. It is not the on-screen quest timer yet |
| % | Share of current party total |

Slot colors follow the in-game party HUD (orange / green / blue / pink).

### Training area

The training area is not a quest, so there is no quest timer and no quest-award table. The overlay switches to a solo mode as soon as you load in:

- Damage is your hooked hits on the pole / wagon (`Damage source: training hits` in the F9 panel).
- The DPS clock starts at your **first hit** after a reset, not when you enter.
- **F7** (or the **Reset training damage** button in the F9 panel) zeroes the damage and re-arms the clock. Leaving the area also resets.
- No fight log is written for free training; time trials are saved (below).

### Time trial

A time trial measures how much damage you do in a fixed window, so different combos or builds can be compared on the pole.

1. Set the duration in the F9 panel under **Time trial** (5–600 s; presets 30 / 60 / 90 / 120 / 180). The value is remembered in `settings.json`.
2. Press **F8** (or **Start trial** in the F9 panel). The overlay shows *armed*; the clock has not started yet.
3. The window starts on your **first hit** after arming and ends exactly `duration` seconds later, measured on the hits' own timestamps, so a hit that lands after the deadline never counts. While running, the overlay shows the countdown, a progress bar (turns red in the last 5 s), running damage / DPS / hits / crits, and your previous best for this weapon and duration.
4. When the window closes the numbers freeze: total damage, DPS, hits, crits, the top five moves with their share, and whether this is a **new personal best** (delta shown). **F8** arms another run with the same settings; **F7** clears it and returns to free training. Leaving the area cancels an unfinished trial.

Every finished trial is saved as a normal fight-log file (`kind: "trial"`, see below) and listed in the F9 **Fight logs** history. **Personal bests** in the F9 panel are per weapon type and duration and come from `index.json`, so they survive restarts.

This needs the hit hook, which is only enabled when the address map matches the game build (`Hit hook: hook 0x…` in the F9 panel). If the panel shows `Hit hook: off`, the training overlay shows a status line explaining that no damage can be counted.

## Settings

`nativePC/plugins/CSharp/MhwDpsMeter/settings.json` stores overlay visibility, opacity, and the time-trial duration. It is written whenever you change one of them in the F9 panel (or toggle the overlay with F10).

To reposition the DPS overlay, open the F9 menu and drag **DPS Meter** by its title bar. Closing the menu makes the overlay click-through again. ImGui remembers the position between sessions.

## Viewers for the logs

See the [mapping audit](data/README.md) for area, weapon-class and monster-name coverage, and the remaining quest, equipment and move-name gaps.

Two independent viewers read the `logs/` folder. Both show the hunt list, party table, cumulative damage, per-sample DPS and rolling DPS curves with enrage/death markers, your per-move breakdown, monsters, the event timeline, and time-trial personal bests with two-run comparison.

| | [`web/`](web/README.md) | [`gpui-viewer/`](gpui-viewer/README.md) |
| --- | --- | --- |
| Stack | SolidJS 2, TanStack Table + Charts, daisyUI 5, Vite | Rust, GPUI via gpui-kit 0.6 (DataTable, Tabs, plot primitives) |
| Runs as | Static web page | Native desktop app **and** in the browser (WebAssembly + WebGPU) |
| Reads logs from | Folder picker (Chromium), drag and drop, `?logs=<url>` | Native folder dialog, CLI argument, `MHW_LOGS`, `?logs=<url>` |
| Start | `cd web && pnpm install && pnpm dev` | `cd gpui-viewer && cargo run --release -- <logs dir>`; browser: `scripts/build-wasm.sh` then `cd www && pnpm dev` |

The native GPUI viewer watches the opened folder every two seconds. New and changed fights
appear automatically, including when the folder starts empty. Refreshes preserve the open
fight, filters and trial comparisons; incomplete writes retain the last readable fight.
The web viewers still load folders/URLs on demand.

The **DPS** chart shows the increase in damage divided by elapsed time between consecutive
samples, with no rolling window. Party samples are normally two seconds apart, so this
chart shows the recorded interval rate rather than exact per-hit timing.

## Diagnostics

**F6** (or the **Dump diagnostics** button in the F9 panel) re-reads the party (even in the hub) and writes `logs/live-debug.json` plus a one-line-per-second `logs/live-debug.log` while in a quest. The F9 panel shows the same data: detected game build and map, party size, per-slot names and raw award damage, the hit-hook call counter, and the monsters the game reports. `scripts/watch-live-debug.sh` tails these from a terminal.

## Fight logs

Every quest (not training sessions, expeditions, or the Guiding Lands) is written as one JSON file next to the plugin, plus a listing file:

```text
nativePC/plugins/CSharp/MhwDpsMeter/logs/YYYY-MM-DD_HHmmss_<questId>_<result>.json      # quests
nativePC/plugins/CSharp/MhwDpsMeter/logs/YYYY-MM-DD_HHmmss_trial<seconds>s_trial.json   # training time trials
nativePC/plugins/CSharp/MhwDpsMeter/logs/index.json
```

`index.json` is a newest-first array of `{file, kind, questId, questName, result, startedAt, durationSeconds, totalDamage, weapon, players[], monsters[]}` so a viewer can list hunts without opening every file. It is rebuilt from the existing files when missing or written by an older version. Recent hunts are also listed under **Fight logs** in the F9 menu.

If the `logs` folder cannot be created, the overlay still runs and a warning is printed to the SharpPluginLoader console.

### Log schema (version 2)

The files are meant to be consumed by an external viewer (a web UI in the style of [relink-logs](https://github.com/villith/relink-logs)). Schema 2 is a superset of the files written by 0.3.0: old files have no `schemaVersion` and lack the new arrays, but every field they do have keeps its name, so one parser handles both.

| Field | Meaning |
| --- | --- |
| `schemaVersion`, `pluginVersion`, `gameBuild` | `2`, the plugin build stamp, and the game build the addresses came from |
| `kind` | `quest` (default when absent) or `trial`. Trials have `questId` 0, `questName` `Time trial 60s`, `result` `trial`, `timerSource` `trial`, a single local player, no monsters, and `samples` rebuilt from the hits |
| `questId`, `questName`, `result`, `stageId`, `stage` | Quest identity. `result` is `complete`, `fail`, `abandon`, `return`, or `leave` |
| `startedAt`, `endedAt`, `durationSeconds`, `timerSource` | UTC timestamps; duration is the plugin's own clock from the in-quest transition (`"local"`), or the configured window for a trial (`"trial"`). `"quest"` is reserved for a future read of the on-screen quest timer |
| `hitCoverage` | `"local"`: `hits` only contains your exact hits. `"party-estimated"`: teammates also have rows, marked `estimated: true` (see below) |
| `rewards` | `{zenny, hunterRankPoints, stars}` as the game reports them for the quest. Item drops are **not** recorded yet: the game exposes no readable table for them and it would need a new hook |
| `players[]` | `slot` (0–3, matches HUD colour), `name`, `isLocal`, `weapon` (local hunter only), `damage`, `dps`, `percent`, `carts` (times that hunter carted; omitted when 0). Sorted by damage |
| `monsters[]` | Large monsters seen: `id` (`m1`, `m2`, … referenced by hits/events), `type`, `name`, `variant`, `maxHealth`, `lastHealth`, `firstSeenT`, `diedT` |
| `samples[]` | `{t, damage[4]}` — cumulative party damage per slot every 2 s (capped at 30 min). This is the only per-player timeline available for other hunters |
| `hits[]` | One row per hit from the deal-damage hook: `t`, `slot`, `monster`, `damage`, `crit`, `tenderized`, `attackId`, `actionSet`, `actionId`, `action` (internal move name when resolvable). Teammate rows carry `estimated: true`, `attackId: -1`, and no crit/tenderize information. Capped at 50 000 |
| `events[]` | Timeline: `enrage` / `unenrage` / `death` / `flinch` (monster, `detail` = flinch action id), `cart` (hunter faint; `slot` when attributed, `detail` = hunter name), `weapon` (slot, `detail` = weapon type), `join` / `leave` (slot, `detail` = hunter name), `slotmatch` (slot, `detail` = how a teammate's hunter entity was matched to the slot). Capped at 5 000 |

All `t` values are seconds on the same clock as `durationSeconds`.

**What per-skill data you can and cannot get.** The game only runs its deal-damage function for hits simulated on this client, so exact individual hits (with crit and tenderize flags) exist for **your own hunter only**. For teammates the plugin *estimates* per-move damage: the game runs every hunter's action controller locally, so their current move is known, and the quest-award damage table is synced live per slot, so every ~0.1 s the increase in a teammate's total is credited to the move they were performing (with a 150 ms carry-over to the previous move, since hits land after animations). Those rows are marked `estimated`. Nothing in memory ties a hunter entity to a party slot, so the link is learned: with one teammate it is immediate, with several it is inferred from which entities were mid-attack when a slot's damage rose (`slotmatch` events say how). Until a slot is matched its damage is not attributed, but totals are always exact. Teammates' weapon types come from their hunter entity once matched.

A same-named monster spawning after the first one died (two Beotodus in Trophy Fishin') gets its own entry: the game re-uses the instance pointer, so the plugin starts a new `m<n>` when a tracked monster's HP jumps back up. `attackId` is the game's attack parameter id and `actionSet`/`actionId` is the action the hunter was in when the hit landed; `action` is the game's internal action name (weapon-specific, not localized). In practice `action` is the key to group by: a dual-blades hunt used only six distinct `attackId` values but dozens of action names (`WP_02::RANBU`, `WP_02::KIJIN_RUSH`, …), and `attackId` 0 covers most normal attacks. A few percent of hits land after the action has already changed (`Common::RUN`, clutch-claw pushes); a viewer can fold those into "other". A viewer that wants friendly move names needs its own lookup table keyed by weapon + action name.

Monster `flinch`, `enrage`, and `death` events are only recorded for tracked large monsters; small monsters are ignored. Hunter `cart` events come from the quest death counter (same source HunterPie uses); the local hunter is attributed when their HP hits zero around the same time, and teammates when a death-like action name is seen on their entity. Unattributed carts still appear on the timeline without a `slot`.

## Limitations

- Teammate per-move damage is an estimate (award-table deltas per 0.1 s credited to the current action); the 2-second award sync granularity and the learned entity-to-slot match both add noise. Item drops and reward items are not recorded: no readable table is known. Teammate cart attribution depends on matching a death-like action to a party slot and can miss some carts (they still appear as unattributed `cart` events).
- Address maps break when Capcom patches the exe. The plugin reads the build number from the `MONSTER HUNTER: WORLD(<build>)` string inside the exe (the exe's version resource is 1.0.0.0, so it cannot be used). Add `Addresses/MonsterHunterWorld.<build>.map` for a new build; HunterPie's `HunterPie/Address/MonsterHunterWorld.<build>.map` has the same keys. If no exact map exists the newest one is used, the hit hook is disabled, and the F9 menu shows `(MISMATCH)`.
- Overlay stays hidden in the hub, expeditions, and the Guiding Lands. Accepting a quest is not enough; you have to depart. The training area is the exception (see above).
- Solo and Challenge Arena often never allocate the quest-award damage table. In that case the meter uses your hooked hits or large-monster HP lost (still includes palico chip on HP).
- The hunt clock is the plugin's own stopwatch from the in-quest transition, not the on-screen quest timer, so it includes the walk from camp. For SOS join-in-progress it starts when **you** load in, not at the quest's start.
- Overlay stays on the hunting map after the quest ends, then hides in the hub.
- Party damage comes from the quest-award table the game syncs for the results screen. The deal-damage hook only ever sees your own hits (the game does not run it for other hunters), so it is just a live local fallback for solo and arena.
- The overlay shows party cart count (`Carts current/max`) from the quest death counter when available.

## Credits

Pointer layout for party names and quest-award damage is documented by community overlays, especially [HunterPie](https://github.com/HunterPie/HunterPie) (Apache-2.0) and [SmartHunter](https://github.com/gabrielefilipp/SmartHunter). This plugin reimplements a small in-process reader; it does not copy their source. In-game loading and ImGui rendering come from [SharpPluginLoader](https://github.com/Fexty12573/SharpPluginLoader).
