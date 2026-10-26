# Combat analytics — research index, architecture, and agent handoff

Main plan: [README](README.md). Semantics: [measurement contract](measurement-contract.md). Runtime evidence: [verification matrix](verification.md). Parent [#14](https://github.com/happypurpleduck/mhw-dps-meter/issues/14), research [#15](https://github.com/happypurpleduck/mhw-dps-meter/issues/15), experimental [PR #16](https://github.com/happypurpleduck/mhw-dps-meter/pull/16).

## Upstream primary references and what each provides

| Source | Specific relevant insight / implementation | Status |
| --- | --- | --- |
| [HunterPie MHWMonster.cs](https://github.com/HunterPie/HunterPie/blob/main/HunterPie.Integrations/Datasources/MonsterHunterWorld/Entity/Enemy/MHWMonster.cs) | Stamina; normal and severable part arrays; ailment pointer list; enrage; tenderize regions; capture threshold | Code reference, **new offsets unverified locally** |
| [HunterPie MHWMonsterPart.cs](https://github.com/HunterPie/HunterPie/blob/main/HunterPie.Integrations/Datasources/MonsterHunterWorld/Entity/Enemy/MHWMonsterPart.cs) | Separate flinch/break/sever data, counter and next threshold semantics | Interpretation reference, verify on game |
| [HunterPie MHWMonsterAilment.cs](https://github.com/HunterPie/HunterPie/blob/main/HunterPie.Integrations/Datasources/MonsterHunterWorld/Entity/Enemy/MHWMonsterAilment.cs) | Status timer, buildup/max and counter semantics | Interpretation reference |
| [HunterPie MHWMonsterPartStructure](https://github.com/HunterPie/HunterPie/blob/main/HunterPie.Integrations/Datasources/MonsterHunterWorld/Definitions/MHWMonsterPartStructure.cs) | Native part struct field ordering for normal/sever readers | Native layout candidate |
| [HunterPie MHWMonsterAilmentStructure](https://github.com/HunterPie/HunterPie/blob/main/HunterPie.Integrations/Datasources/MonsterHunterWorld/Definitions/MHWMonsterAilmentStructure.cs) | Native owner, id, max, buildup, duration, counter | Native layout candidate |
| [HunterPie MHWMonsterStatusStructure](https://github.com/HunterPie/HunterPie/blob/main/HunterPie.Integrations/Datasources/MonsterHunterWorld/Definitions/MHWMonsterStatusStructure.cs) | Enrage status fields | Not yet implemented |
| [HunterPie World MonsterData.xml](https://github.com/HunterPie/HunterPie/blob/main/HunterPie/Game/World/Data/MonsterData.xml) | Per-monster canonical part definitions, break thresholds, tenderize ids, ailment IDs, capture threshold | Existing 72/653 mapping enriched by #16 |
| [HunterPie 421810 address map](https://github.com/HunterPie/HunterPie/blob/main/HunterPie/Address/MonsterHunterWorld.421810.map) | Existing MHW global addresses and damage display function | Cross-reference (not blanket validation) |
| [SPL Monster.cs](https://github.com/Fexty12573/SharpPluginLoader/blob/master/SharpPluginLoader.Core/Entities/Monster.cs) | Monster identity, lifecycle, enrage, flinch, death; existing host integration | This project already uses SPL |
| [SmartHunter](https://github.com/r00telement/SmartHunter) | Independent legacy monster stats/DPS overlay and an authority cross-check candidate | Compare, do not blindly duplicate behavior |
| [HunterPie.SyncPlugin](https://github.com/amadare42/HunterPie.SyncPlugin) | Reference approach for synchronizing monster part/status observations where guest client data is inadequate | Optional future only, never mandatory |
| [Kiranico MHW](https://mhworld.kiranico.com/) | Monster physiology, status thresholds, mechanics and damage formulas | Static mechanics; not proof of current per-hit runtime value |
| [MHW database](https://docs.mhw-db.com/) | Game lookup/database and motion value research where available | Optional later raw/element research |

**Source/licensing:** The project's `data/README.md` records canonical game-name and part mapping provenance, including HunterPie-derived metadata. Keep compatible attribution/license notices if future import expands upstream content. Upstream code is **research**, not a proven substitute for verification of the binary running on this computer.

## Local architecture / existing facts

- `src/MhwDpsMeter/Plugin.cs`: SharpPluginLoader callbacks; ~100ms poll; quest lifecycle and timer; F9/F10 settings/overlay; event hooks.
- `MonsterHpTracker.cs`: enumerated monsters and current/max HP; pointer may be recycled and reader banks despawned damage.
- `DamageTracker.cs`: per-hit damage-number callback via `FUN_DEAL_DAMAGE`; 421810 `FUN_SHOW_DAMAGE_CONTEXT` scoped collision hook; **only local exact damage/part tags**.
- `MonsterPartResolver.cs` and `MonsterParts.cs`: normal-array collision ordinal → canonical part id; alternate part selector after break; renamed/enriched definitions.
- `PartyDamageReader.cs`, `PartyActionTracker.cs`: quest-award per-slot totals and **estimated** teammate action-linked deltas; neither offers exact remote part damage.
- `HuntRecorder.cs`, `FightLog.cs`, `FightLogStore.cs`: monster id mapping, exact local hits, events, samples and JSON logs; #16 adds optional schema-3 gaugeSamples.
- `Overlay.cs`, `PluginSettings.cs`: ImGui overlay + F9 controls; #16 adds experimental preview and off-by-default setting.
- `web/src/schema/fightlog.ts`, `web/src/data/parse.ts`: SolidJS parser/types; `gpui-viewer/src/model.rs`: Rust/Serde parser. In #16 these accept data but do not yet graph gauges.
- `tests/PluginRegression`: isolated C# production reader + simulated game/memory regressions; OS-native safety runner exists. This is **not in-game proof**.
- `docs/part-damage.md`: exact verified 421810 call path from application function to collision context to display function; 34/34 tagged hits in an incomplete historical smoke, plus multiplayer attribution limits.
- `docs/item-damage.md`: cannot currently know who placed/detonated bombs, source entity or chain reaction; never infer from action proximity/damage amount.

## Important pitfalls

1. **Wrong hook:** `FUN_DEAL_DAMAGE` displays damage numbers, it does not update flinch/ailment meters; its position parameter is a Vector3. Hooking it as if it were the part application function produced 0 part tags historically. The separate collision-context hook now works for local part mapping. To capture *buildup by attack*, research the genuine damage application path described in [part-damage](../part-damage.md), especially around 421810 `0x1402C66B0` and associated status writers.
2. **Normal vs sever:** a normal part collision index is NOT a canonical id or sever index. For example Anjanath normal slot 0 is head id 2, not severable tail id 0; use original XML order skipping sever entries. Same nominal body-part name can exist in two independent part-meter lists.
3. **Host authority:** reading an address on a guest does not make it authoritative. Compare solo/host/guest and consider sync only after verified data origin and explicit consent.
4. **Partbreaker:** measured displayed HP damage and part buildup are not equivalent; do not apply guessed multipliers to label numbers exact.
5. **Stamina vs exhaust vs KO:** three distinct measurements; don't merge them or call exhaust buildup the current stamina pool.
6. **Counters and timers:** exact ailment status fields differ from enrage struct; observed gauge drop/reset might mean proc or despawn/decay/stale read. Event timing at poll resolution is approximate.
7. **Multiplayer item attribution:** bomb placer, detonator, chain reactions, environment and Palico need verified source tracing. Do not subtract guessed item damage from quest awards.
8. **Tests:** simulated stubs validate arithmetic and bounds, never a live pointer layout or unverified detour. Preserve source quality in logs and viewers.
9. **Special monsters:** Alatreon elemental topple mechanics and multi-phase encounters need separate verified layout; don't treat every boss-specific counter as an ordinary flinch meter.
10. **Schema/name precision:** avoid calling per-hunt `gaugeSamples` a damage split; parser compatibility ≠ complete charts. Confirm exact C# JSON naming and TS/Rust field correspondence with fixtures.

## Agent execution contract

The work is approved as a **feature initiative**, not as authorization to merge unverified memory hooks. Work autonomously in small focused PRs; request human input only where an actual user/game-run decision or runtime access is essential.

1. Inspect live `master`, draft #16 and issues #14–#20; use docs above as the shared goal, do not redo scope debates. Rebase carefully as main evolves.
2. Start with #17: verify data layouts, pointer lifetimes and host/guest authority in real game. If no game host/runtime is accessible, implement static safety/test improvements and label the live gate **BLOCKED**, never claim fully complete.
3. Follow #18 (normal/sever/tenderize/enrage/event semantics), #19 (both viewers + backward-compatible fixtures), #20 (configurable live overlay). Stacked PRs are acceptable when they isolate responsibility and dependency; do not block independent viewer improvements on reverse engineering.
4. Keep #15 independent as a research spike with concrete disassembly/capture notes and hypotheses marked as hypotheses; only open an implementation PR after a verified application/source hook is demonstrated.
5. For every PR: scope, implementation summary, known limitations, exact game-build support, tests run, evidence links, CI status, release gate, and next issue. Only mark tasks complete upon evidence. Don't silently close #14 before the full definition of done.
6. Keep work read-only, bounded, resilient to unsupported build and invalid pointers. No new mandatory server, hook replacing established DPS, or game-state manipulation.
7. Confirm Windows and Proton support. Existing features, training/time trials, F9/F10 and log parsers remain functional; do not sacrifice them for monster gauges.
8. Provide an end-of-slice report linked on the parent issue and PR with remaining unanswered questions and a clear release readiness decision. If no human game runtime, continue other independent work rather than fabricating validation.

## Priority and deferral policy

Highest: #17 safety and actual correctness; then #18 supported measurement and #19 useful analysis; then #20 UI polish. #15 exact attribution and optional sync are distinct higher-risk spikes; capture findings but do not delay achievable, validated status tracking.

**Not yet commitments**: fully accurate teammate per-hit attribution without participants running instrumented clients, real raw/element damage decomposition from the game's calculation path, item/bomb placer credit, and every specialized boss mechanic. Track evidence of feasibility before promising results.
