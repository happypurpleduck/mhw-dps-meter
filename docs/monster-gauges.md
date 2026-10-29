# Experimental monster gauges (first implementation slice)

**Status:** draft, opt-in. This is *not* a proven host-authoritative reading and must not be enabled by default until verified against a running supported build. Phase tracking: #14. Attribution research: #15.

## Scope

The plugin adds a protected, read-only, bounded `MonsterGaugeReader` for **confirmed MHW build 421810 only**, gated by `experimentalMonsterGauges` in `settings.json` (F9 checkbox). An unsupported/unconfirmed build never polls these extra structures. Missing/invalid readings remain absent; an unavailable meter is not treated as zero.

- Monster stamina: current and maximum at entity `+0x1C0F0`.
- Normal part flinch/break meters: monster part-controller pointer at `+0x1D058`, normal array `+0x40 + slot*0x1F8`; max, remaining, counter. Collision ordinal resolves to the canonical part id in `monster-parts.json`, skipping severable definitions. Next break threshold uses HunterPie metadata.
- Ailments: bounded pointer list at monster `+0x1BC40`, struct at ailment pointer `+0x148`, validating owner, id, buildup, max buildup, current/maximum duration, proc count. Includes KO/stun and exhaust buildup.
- The in-game overlay shows only a small preview of active ailments and the next-break part gauges. All readings are visibly labelled experimental/non-authoritative. It does **not** log state samples yet.

## Explicitly deferred

- Separate severable part memory region (`+0x1FC8`), tenderization-duration array (`+0x1C458`), enrage state, exact proc timing, per-hit buildup, host/guest authority detection, production widget polish, fight-log schema + both viewers.
- Current collision hit damage continues to mean **HP damage by contact part**; it is not part buildup, KO, or ailment damage.
- No game-memory writes, remote service, or new detours.

## Research basis

Offsets follow the [HunterPie MHWMonster reader](https://github.com/HunterPie/HunterPie/blob/main/HunterPie.Integrations/Datasources/MonsterHunterWorld/Entity/Enemy/MHWMonster.cs) and its [part](https://github.com/HunterPie/HunterPie/blob/main/HunterPie.Integrations/Datasources/MonsterHunterWorld/Entity/Enemy/MHWMonsterPart.cs) and [ailment](https://github.com/HunterPie/HunterPie/blob/main/HunterPie.Integrations/Datasources/MonsterHunterWorld/Entity/Enemy/MHWMonsterAilment.cs) structures. `data/monster-parts.json` was enriched from HunterPie's Apache-2.0 World MonsterData.xml. These are **reference layouts**, not evidence they work in this binary without verification.

## Live verification required

1. On 421810, load a solo Anjanath and compare stamina, head/leg flinch gauges and KO status with HunterPie. Ensure data is read while entering/leaving quest and on a reused monster pointer.
2. Use a Hammer for KO, elemental-status weapon for paralysis/poison, part break with/without Partbreaker; watch actual gauge resets and counters.
3. Compare solo, multiplayer host and multiplayer guest. Do not treat guest values as authoritative without independent confirmation.
4. Run under Windows and Proton, with and without the feature enabled. Confirm the plugin still loads and games never crash during rapid despawns or quest load.
5. After these pass, proceed to #14 log capture/viewers and full gauge widget; keep #15 distinct.

Run `dotnet run --project tests/PluginRegression` for simulated-reading regressions, **not** a substitute for in-game validation.
