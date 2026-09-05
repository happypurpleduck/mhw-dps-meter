# MHW DPS Meter

In-game damage overlay for Monster Hunter World: Iceborne. It is a [SharpPluginLoader](https://fexty12573.github.io/SharpPluginLoader/) C# plugin, so it draws on the game’s own swapchain and works under Proton on Linux. There is no external overlay process.

During a quest it shows each hunter’s **name**, **total damage**, **DPS**, and **% of party damage**. When the quest ends it writes a JSON fight log (summary plus a 2-second damage sample). In the **training area** it shows your own damage and DPS against the pole, with **F7** to reset without leaving.

## Requirements

- Monster Hunter World: Iceborne (Steam), game **15.23**. Address maps ship for builds **421810** (current Steam build) and **421631**. The build is the number in the game window title, `MONSTER HUNTER: WORLD(421810)`.
- [SharpPluginLoader](https://github.com/Fexty12573/SharpPluginLoader/releases) — **Windows** release (`winmm.dll`) for Windows, **Linux** release (`ucrtbase.dll`) for Proton/Wine.
- .NET Desktop Runtime 8.0 (**x64** on Windows; **inside the Proton prefix** on Linux — `protontricks 582010 dotnetdesktop8`)
- Optional: [Stracker’s Loader](https://www.nexusmods.com/monsterhunterworld/mods/1982) if you also use native `nativePC` mods. This overlay does **not** need it, and does **not** need CRCBypass.

## Windows install

1. Install [.NET 8.0 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0/runtime) — pick **.NET Desktop Runtime**, not just the .NET Runtime.

2. Download [SharpPluginLoader](https://github.com/Fexty12573/SharpPluginLoader/releases) **Windows** release (`SharpPluginLoader-<version>.zip`) and extract it into the game root (the folder containing `MonsterHunterWorld.exe`). You should have `winmm.dll` beside the exe and a `nativePC/plugins/CSharp/` folder. If the game closes immediately on launch without Steam, add `MonsterHunterWorld.exe` as a Non-Steam Game and launch through Steam (the Steam overlay changes `winmm.dll` load order).

3. Download `MhwDpsMeter-0.3.0.zip` from this repo’s [Releases](https://github.com/anomalyco/mhw-dps-meter/releases) (or build it — see below) and **extract it into the same game root**. It creates:

   ```text
   nativePC/plugins/CSharp/MhwDpsMeter/MhwDpsMeter.dll
   nativePC/plugins/CSharp/MhwDpsMeter/Addresses/MonsterHunterWorld.421810.map
   nativePC/plugins/CSharp/MhwDpsMeter/Addresses/MonsterHunterWorld.421631.map
   ```

   Copy **only** that folder — do not duplicate the DLL directly into `CSharp/` and do not put it under `CSharp/Loader/`.

4. Optional: extract Stracker’s Loader into the game root (`dinput8.dll`) if you use other `nativePC` mods.

5. Launch the game. A SharpPluginLoader console should appear. Press **F9** — you should see **DPS Meter** with a status line. The first line, `Plugin build: 0.3.0+<UTC build time>`, tells you which DLL is actually running; if it does not match the one you copied, quit and relaunch. `Game build:` should name the map matching your exe. Depart on a quest; overlay is top-right. **F10** toggles it.

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

5. Download `MhwDpsMeter-0.3.0.zip` and extract it into the game root (same layout as Windows):

   ```text
   nativePC/plugins/CSharp/MhwDpsMeter/MhwDpsMeter.dll
   nativePC/plugins/CSharp/MhwDpsMeter/Addresses/MonsterHunterWorld.421810.map
   nativePC/plugins/CSharp/MhwDpsMeter/Addresses/MonsterHunterWorld.421631.map
   ```

6. Launch the game. A SharpPluginLoader console should appear. Press **F9** — you should see **DPS Meter** with a status line. The first line, `Plugin build: 0.3.0+<UTC build time>`, tells you which DLL is actually running; if it does not match the one you copied, quit and relaunch. `Game build:` should name the map matching your exe. Depart on a quest; overlay is top-right. **F10** toggles it.

If F9 does nothing, SPL still is not injecting. Check that `ucrtbase.dll` exists in the game root and that launch options include `ucrtbase=n,b`. Loader logs go next to the exe when `loader-config.json` has `"logfile": true`.

SharpPluginLoader reloads a plugin when its DLL changes. Under Proton that watcher often misses the copy, so quit the game fully and relaunch after updating the plugin.

## Build

Needs the .NET 8 SDK (`dotnet --list-sdks` should show 8.0.x).

```bash
dotnet build -c Release
# or: scripts/package.sh   # builds and zips dist/MhwDpsMeter-0.3.0.zip
```

Output:

```text
dist/Release/nativePC/plugins/CSharp/MhwDpsMeter/   # raw build
dist/MhwDpsMeter-0.3.0.zip                            # game-root zip (same DLL works on Windows + Proton)
```

Extract the zip into the game root (beside `MonsterHunterWorld.exe`). The plugin DLL is the same on both platforms — only the SharpPluginLoader injector differs (`winmm.dll` on Windows, `ucrtbase.dll` on Proton).

## Overlay

| Column | Meaning |
| --- | --- |
| Name | Party slot name (`*` = you) |
| Damage | Per-hunter quest-award total (the results-screen value). Solo/arena without that table falls back to your live hits or large-monster HP lost |
| DPS | `damage / in-game quest timer` (same clock for every hunter). Falls back to time since you entered if the quest timer is not running |
| % | Share of current party total |

Slot colors follow the in-game party HUD (orange / green / blue / pink).

### Training area

The training area is not a quest, so there is no quest timer and no quest-award table. The overlay switches to a solo mode as soon as you load in:

- Damage is your hooked hits on the pole / wagon (`Damage source: training hits` in the F9 panel).
- The DPS clock starts at your **first hit** after a reset, not when you enter.
- **F7** (or the **Reset training damage** button in the F9 panel) zeroes the damage and re-arms the clock. Leaving the area also resets.
- No fight log is written for training sessions.

This needs the hit hook, which is only enabled when the address map matches the game build (`Hit hook: hook 0x…` in the F9 panel). If the panel shows `Hit hook: off`, the training overlay shows a status line explaining that no damage can be counted.

## Diagnostics

**F6** (or the **Dump diagnostics** button in the F9 panel) re-reads the party (even in the hub) and writes `logs/live-debug.json` plus a one-line-per-second `logs/live-debug.log` while in a quest. The F9 panel shows the same data: detected game build and map, party size, per-slot names and raw award damage, the hit-hook call counter, and the monsters the game reports. `scripts/watch-live-debug.sh` tails these from a terminal.

## Fight logs

Written next to the plugin (quests only, not training sessions):

```text
nativePC/plugins/CSharp/MhwDpsMeter/logs/YYYY-MM-DD_HHmmss_<questId>_<result>.json
```

Each file has the quest name, result, duration, per-player damage / DPS / %, and a damage-over-time sample every 2 seconds (capped at 30 minutes). Recent hunts are listed under **Fight logs** in the F9 menu.

If the `logs` folder cannot be created, the overlay still runs and a warning is printed to the SharpPluginLoader console.

## Limitations

- Address maps break when Capcom patches the exe. The plugin reads the build number from the `MONSTER HUNTER: WORLD(<build>)` string inside the exe (the exe's version resource is 1.0.0.0, so it cannot be used). Add `Addresses/MonsterHunterWorld.<build>.map` for a new build; HunterPie's `HunterPie/Address/MonsterHunterWorld.<build>.map` has the same keys. If no exact map exists the newest one is used, the hit hook is disabled, and the F9 menu shows `(MISMATCH)`.
- Overlay stays hidden in the hub, expeditions, and the Guiding Lands. Accepting a quest is not enough; you have to depart. The training area is the exception (see above).
- Solo and Challenge Arena often never allocate the quest-award damage table. In that case the meter uses your hooked hits or large-monster HP lost (still includes palico chip on HP).
- SOS join-in-progress DPS uses the **quest timer** (HunterPie’s default). Your personal DPS looks lower than “time since I joined” because the clock includes the hunt before you arrived.
- Overlay stays on the hunting map after the quest ends, then hides in the hub.
- Party damage comes from the quest-award table the game syncs for the results screen. The deal-damage hook only ever sees your own hits (the game does not run it for other hunters), so it is just a live local fallback for solo and arena.

## Credits

Pointer layout for party names and quest-award damage is documented by community overlays, especially [HunterPie](https://github.com/HunterPie/HunterPie) (Apache-2.0) and [SmartHunter](https://github.com/gabrielefilipp/SmartHunter). This plugin reimplements a small in-process reader; it does not copy their source. In-game loading and ImGui rendering come from [SharpPluginLoader](https://github.com/Fexty12573/SharpPluginLoader).
