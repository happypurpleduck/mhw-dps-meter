# MHW DPS Meter

In-game damage overlay for Monster Hunter World: Iceborne. It is a [SharpPluginLoader](https://fexty12573.github.io/SharpPluginLoader/) C# plugin, so it draws on the game’s own swapchain and works under Proton on Linux. There is no external overlay process.

During a quest it shows each hunter’s **name**, **total damage**, **DPS**, and **% of party damage**. When the quest ends it writes a JSON fight log (summary plus a 2-second damage sample).

## Requirements

- Monster Hunter World: Iceborne (Steam), currently mapped for file version **421631** (game **15.23**)
- [SharpPluginLoader](https://github.com/Fexty12573/SharpPluginLoader/releases) **Linux** zip (`ucrtbase.dll`)
- .NET Desktop Runtime 8.0 **inside the Proton prefix**
- Optional: [Stracker’s Loader](https://www.nexusmods.com/monsterhunterworld/mods/1982) if you also use native `nativePC` mods. This overlay does **not** need it, and does **not** need CRCBypass.

## Linux / Proton install

1. Install prefix dependencies:

   ```bash
   protontricks 582010 dotnetdesktop8 d3dcompiler_47
   ```

2. Extract the SharpPluginLoader Linux release into the game root (same folder as `MonsterHunterWorld.exe`). You should see `ucrtbase.dll`.

3. Optional: extract Stracker’s Loader into that same folder (`dinput8.dll`).

4. Steam launch options:

   ```text
   WINEDLLOVERRIDES="ucrtbase=n,b" %command%
   ```

   With Stracker’s Loader as well:

   ```text
   WINEDLLOVERRIDES="ucrtbase,dinput8=n,b" %command%
   ```

   If the game fails to start because a system `DOTNET_ROOT` leaks into Proton:

   ```text
   DOTNET_ROOT= WINEDLLOVERRIDES="ucrtbase,dinput8=n,b" %command%
   ```

5. Copy the plugin folder into the game:

   ```text
   nativePC/plugins/CSharp/MhwDpsMeter/MhwDpsMeter.dll
   nativePC/plugins/CSharp/MhwDpsMeter/Addresses/MonsterHunterWorld.421631.map
   ```

   After a Release build those files are in `dist/Release/nativePC/plugins/CSharp/MhwDpsMeter/`.

6. Launch the game. Press **F9** for the SharpPluginLoader menu (DPS Meter settings and fight logs). Depart on a quest; the overlay appears in the top-right. **F10** toggles it.

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
| Name | Party slot name (`*` = you) |
| Damage | Quest-award total (the same number the game uses at the end screen) |
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
- Expeditions and the Guiding Lands do not fill the quest-award damage table; the overlay stays hidden and no log is written.
- SOS join-in-progress DPS is relative to **when you entered**, not the host’s quest timer.
- Only weapon damage the game itself counts (same as the quest awards screen).
- Read-only memory. No function hooks, no CRCBypass.

## Credits

Pointer layout for party names and quest-award damage is documented by community overlays, especially [HunterPie](https://github.com/HunterPie/HunterPie) (Apache-2.0) and [SmartHunter](https://github.com/gabrielefilipp/SmartHunter). This plugin reimplements a small in-process reader; it does not copy their source. In-game loading and ImGui rendering come from [SharpPluginLoader](https://github.com/Fexty12573/SharpPluginLoader).
