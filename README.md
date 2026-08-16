# MHW DPS Meter

In-game damage overlay for Monster Hunter World: Iceborne. It is a [SharpPluginLoader](https://fexty12573.github.io/SharpPluginLoader/) C# plugin, so it draws on the game’s own swapchain and works under Proton on Linux. There is no external overlay process.

During a quest it shows each hunter’s **name**, **total damage**, **DPS**, and **% of party damage**. When the quest ends it writes a JSON fight log (summary plus a 2-second damage sample).

## Requirements

- Monster Hunter World: Iceborne (Steam), currently mapped for file version **421631** (game **15.23**)
- [SharpPluginLoader](https://github.com/Fexty12573/SharpPluginLoader/releases) **Linux** zip (`ucrtbase.dll`)
- .NET Desktop Runtime 8.0 **inside the Proton prefix**
- Optional: [Stracker’s Loader](https://www.nexusmods.com/monsterhunterworld/mods/1982) if you also use native `nativePC` mods. This overlay does **not** need it, and does **not** need CRCBypass.

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

5. Copy **only** the plugin folder (not a second copy in `CSharp/`):

   ```text
   nativePC/plugins/CSharp/MhwDpsMeter/MhwDpsMeter.dll
   nativePC/plugins/CSharp/MhwDpsMeter/Addresses/MonsterHunterWorld.421631.map
   ```

6. Launch the game. A SharpPluginLoader console should appear. Press **F9** — you should see **DPS Meter** with a status line. Depart on a quest; overlay is top-right. **F10** toggles it.

If F9 does nothing, SPL still is not injecting. Check that `ucrtbase.dll` exists in the game root and that launch options include `ucrtbase=n,b`. Loader logs go next to the exe when `loader-config.json` has `"logfile": true`.

SharpPluginLoader reloads a plugin when its DLL changes. Under Proton that watcher often misses the copy, so quit the game fully and relaunch after updating the plugin.

## Build

Needs the .NET 8 SDK (`dotnet --list-sdks` should show 8.0.x).

```bash
dotnet build -c Release
```

Output:

```text
dist/Release/nativePC/plugins/CSharp/MhwDpsMeter/
```

## Overlay

| Column | Meaning |
| --- | --- |
| Name | Party slot name, or your save name when the party list is empty (`*` = you) |
| Damage | Quest-award total when the online session table exists; otherwise HP lost on spawned monsters |
| DPS | `damage / seconds since you entered the quest` |
| % | Share of current party total |

Slot colors follow the in-game party HUD (orange / green / blue / pink).

## Fight logs

Written next to the plugin, where Proton can create files:

```text
nativePC/plugins/CSharp/MhwDpsMeter/logs/YYYY-MM-DD_HHmmss_<questId>_<result>.json
```

Each file has the quest name, result, duration, per-player damage / DPS / %, and a damage-over-time sample every 2 seconds (capped at 30 minutes). Recent hunts are listed under **Fight logs** in the F9 menu.

If the `logs` folder cannot be created, the overlay still runs and a warning is printed to the SharpPluginLoader console.

## Limitations

- Address maps break when Capcom patches the exe. Add a new `Addresses/MonsterHunterWorld.<FilePrivatePart>.map` for that build.
- Overlay stays hidden in the hub, expeditions, and the Guiding Lands. Accepting a quest is not enough; you have to depart.
- Solo and Challenge Arena often never allocate the quest-award damage table. In that case the meter uses monster HP lost (includes palico hits) and cannot split party damage.
- SOS join-in-progress DPS is relative to **when you entered**, not the host’s quest timer.
- Read-only memory. No function hooks, no CRCBypass.

## Credits

Pointer layout for party names and quest-award damage is documented by community overlays, especially [HunterPie](https://github.com/HunterPie/HunterPie) (Apache-2.0) and [SmartHunter](https://github.com/gabrielefilipp/SmartHunter). This plugin reimplements a small in-process reader; it does not copy their source. In-game loading and ImGui rendering come from [SharpPluginLoader](https://github.com/Fexty12573/SharpPluginLoader).
