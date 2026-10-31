# Combat analytics — runtime verification and evidence matrix

**Owner issue:** [#17](https://github.com/happypurpleduck/mhw-dps-meter/issues/17), prerequisite for release of [draft #16](https://github.com/happypurpleduck/mhw-dps-meter/pull/16). No checked-off runtime test below means it has passed: **no gameplay validation has yet been attached** as of 2026-10-26. Green CI does not establish correctness of game-memory layouts.

## Working build and truth tiers

- Game: Monster Hunter: World Iceborne Steam; prototype reads only on **confirmed 421810**, with F9 **Experimental monster gauges** enabled. Existing address map also contains 421631 for other functionality; experimental gauges do not run there.
- Upstream reference: HunterPie's MHW monster entity and C# structs. The offset numbers below are *candidate layouts copied from the upstream implementation*; they were **not** proven by this PR on a live 421810 executable.
- Existing independently documented 421810 collision-context hook and its hit source are covered in [part-damage.md](../part-damage.md). Do not mistake those verified hit-path addresses for verification of the newly added ailment/part-gauge readers.
- Runtime measurements should be verified with live controlled conditions and a second observability method (HunterPie comparison, native debugger/traces as appropriate), not one sampled value.

## Candidate native field map (all `+offset` relative to the named base)

| Field | Candidate reader layout | Source | Current proof |
| --- | --- | --- | --- |
| Monster HP | entity pointer+0x7670→struct max+0x60/current+0x64 | SPL/HunterPie | pre-existing code, outside new validation |
| Stamina | monster +0x1C0F0 = current float; +0x1C0F4 = maximum float | HunterPie MHWMonster.GetMonsterStaminaData | **unverified in new draft** |
| Part-controller pointer | monster +0x1D058 | HunterPie MHWMonster.GetMonsterParts | **unverified** |
| Normal part slot | controller +0x40 + index*0x1F8 (index skips severable definitions) | HunterPie MHWMonster.GetMonsterParts | **unverified** |
| Normal part fields | normal part struct max+0x0C, remaining+0x10, counter+0x18 | HunterPie MHWMonsterPartStructure | **unverified** |
| Ailment pointer list | monster +0x1BC40; up to 32 pointers, 8B apart (draft cap) | HunterPie MHWMonster.GetMonsterAilments | **unverified** |
| Ailment value struct | list pointer +0x148; owner+0x00, ailment id+0x10, max duration+0x14, buildup+0x30, max buildup+0x40, duration+0x70, counter+0x78 | HunterPie MHWMonsterAilmentStructure | **unverified** |
| Enrage | monster +0x1BE30, MHWMonsterStatusStructure | HunterPie MHWMonster.GetMonsterEnrage | deferred |
| Tenderize regions | monster +0x1C458, ten MHWTenderizeInfoStructure | HunterPie MHWMonster.GetMonsterPartTenderizes | deferred |
| Severable part array | part controller +0x1FC8, separate from normal | HunterPie MHWMonster.GetMonsterParts | deferred: independently bound and validate scan |
| Hit context | FUN_SHOW_DAMAGE_CONTEXT and FUN_DEAL_DAMAGE map entries, with collision slot and alternate selection | existing docs/part-damage.md and runtime tests | **already verified for 421810 collision tagging**, not buildup |

Some offsets are read from structs with custom layout/alignment. Validate actual stride and each field, not just numerical plausibility. A meter value equal to some plausible number can still be a wrong offset, false owner or stale guest copy.

## Safety and data semantics checks

- Strict build identity and safe fallback: only exact supported build and known map; disabled/default mode adds no new memory polls; no best-guess map on an unknown executable. Note current `ApplyMap` can load fallback maps for unrelated legacy features — that does not authorize experimental reads.
- Never directly dereference stale memory; use `SafeMemory.TryReadProtected` or equivalently safe OS copy, bounded reads, valid user-pointer checks, alignment/owner/struct bounds. No game writes or unverified function detours.
- Bound all arrays and traversals, notably HunterPie upstream sever loop requires independent scrutiny (avoid unbounded pointer walk). Distinguish deleted monster vs transient unreadable monster; do not silently retain stale values.
- Structural invariants: finite float; max > 0 where appropriate; current in validated range; counter sane; owner matches expected monster pointer; known vs unknown ailment id; normal/sever namespace mapping.
- Generation: on monster reuse/respawn, new fight-local id (`m2`) and reset historical counters. Don't leak previous monster state/party identity into current fight. Reset on quest exit/reload.
- Time: emit observation timestamps from hunt clock; timestamps of counter changes seen in poll are *not* precise in-between proc times.
- Host authority: local values on a multiplayer guest may be stale/non-authoritative. Record exactly what was observed and label `local-unverified`; do not call it verified until validated against actual host. Host/solo identification itself must be verified.
- Snapshot performance: no explosion of per-poll allocations/IO; bound samples (initial draft two seconds per monster, 3600 overall), monitor game frame time, CPU, memory, log size on long hunts.

## Live acceptance matrix (all currently unchecked)

| ID | Environment & encounter | Required evidence/result |
| --- | --- | --- |
| V01 | Windows, solo Anjanath on 421810 | HP + stamina compare, normal collision part ordinals and nonzero gauges |
| V02 | Anjanath head with 5 flinch cycles, legs, head break | break threshold semantics, counter reset / staged break |
| V03 | Rathalos head, tail, wing | no mapping drift and separate normal/sever logic |
| V04 | Hammer KO attacks/actual stun | ailment id=7, buildup/max/proc and active duration, no false hunter attribution |
| V05 | Poison and paralysis with decay/procs | id mapping, progression, duration, resets, skipped polls |
| V06 | Exhaust attacks vs monster stamina | two independent values; valid causality not asserted from simultaneous change |
| V07 | Sever tail with severing weapon | sever meter identity and event, no normal-slot collision |
| V08 | Partbreaker on/off | HP hit damage separated from flinch/break progress |
| V09 | Tenderize + wait for expiry | region id mapping, timer direction, hit tenderize separate |
| V10 | Enrage/unenrage with SPL callbacks | status duration/counter and event deduplication |
| V11 | Two alive monsters and quest join-in-progress | stable fight ids, correct hit targeting and sample routing |
| V12 | Same monster pointer reused, new instance | no inherited old gauges, `m2` identity |
| V13 | Solo vs multiplayer host vs guest | values/authority differences recorded; guests never silently claimed exact |
| V14 | Windows quest load / failure / abandon / restart, feature off/on | no crash, stale record, duplicate completion or background poll |
| V15 | Linux Proton and SPL `ucrtbase.dll` injector | same safety/behavior, no load/crash/regression |
| V16 | Training area / time trials F7/F8; party DPS | pre-existing metrics unaffected; no phantom monster gauges |
| V17 | Unreadable/invalid pointer, huge counter, non-finite values | clean omission and diagnostics, never 0 masquerading as a reading |
| V18 | Longer multi-monster hunt | bounded JSON size, history readers, CPU/frame overhead |

All rows require observed result, pass/fail, build/runtime evidence. Not every test can run without the game; incomplete rows must remain explicitly unverified.

## Evidence template for GitHub issue / PR

~~~text
Test ID:
Date / tester:
Plugin commit and build stamp:
MonsterHunterWorld executable game build (and hash if possible):
SPL version + loader type:
Windows version or Proton version + compositor:
Solo / host / guest / joined-late:
Monster / quest / weapon / target part / ailment:
Expected behavior from an independent reference:
Recorded actual state and timeline (include sample JSON or F6 debug excerpt):
Observed differences, transient read failures, proc/reset anomalies:
Crash/freezes/frametime regression:
Status: PASS / FAIL / INCONCLUSIVE / NOT RUN
Attachments / file link:
Follow-up fix issue/commit:
~~~

Avoid publishing personally identifying party names, save details or private telemetry in public evidence.

## CI versus runtime

As of 2026-10-26, [CI run 40233131193](https://github.com/happypurpleduck/mhw-dps-meter/actions/runs/40233131193) for draft #16 commit `d25e015db729bfbe6e86b7fb97ffea6701cf075a` reported pass for plugin Ubuntu 24.04 / Windows 2022; Viewer Linux / Windows; Release tooling. This is a **static/test pass**, not gameplay validation and not applicable to later commits until rerun. Existing `tests/PluginRegression` simulated structs and native memory regression are useful safety checks but do not prove addresses or host behavior.

## Merge gate

A PR touching new memory reads must remain draft/unreleased until test evidence, build compatibility and provenance are documented. If real runtime validation cannot be performed, keep code opt-in/unreleased or a research-only branch; document precisely why it is not release-ready. Never weaken crash prevention or falsely claim verification just to clear a checklist.


## Guiding Lands mandatory follow-up (not yet run)

These checks apply to stage 109 (The Guiding Lands), in addition to V01–V18. The normal synchronized quest-award damage table is unavailable: remote damage **must remain unknown**, not zero or invented.

| ID | Environment | Required runtime proof |
| --- | --- | --- |
| GL01 | Windows solo enter/leave Guiding Lands | stage-based start with or without positive quest ID, valid player guard, no phantom quest-results screen, local-hook totals only |
| GL02 | Windows host with one to three guests | independent local hook total only, no fake teammate rankings/award values, monster gauges visibly unverified |
| GL03 | Windows guest/late join | stage entry and local identity/name, correct source labels, no invented remote DPS, no host-authoritative gauge claim |
| GL04 | Solo/host/guest monster kill → lure → despawn → spawn | one continuous expedition session and monotonic clock; new fight-local monster IDs, no cached stale gauge or previous-monster contamination |
| GL05 | Player cart, fast travel between Guiding Lands camps, party join/leave | no premature session end or duplicate log, no invalid manual gauge selection, no phantom teammate totals |
| GL06 | Exit to Seliana and re-enter within three minutes | one finished log per real expedition, no quest-id cooldown blocking re-entry, proper reset of hook damage, poller and selection |
| GL07 | Windows/Proton, 421810 confirmed, capture off/on | no extra disabled native reads, source-labeled fresh/stale gauges, native pointer failures fail closed, 100-ms/1-s cadence unchanged |
| GL08 | Solo and multiplayer under Windows and Proton | real overlay position/legibility, FPS/frame-time and startup/teardown smoke; attach anonymized logs/screenshots |

Keep all GL rows **NOT RUN** until real game testing. Source-level simulated model checks are not substitutes.

### Guiding Lands stage lifecycle source regression (not in-game evidence)

The production `SyncQuestState` path now uses the pure `GuidingLandsSession.Decide(stage, player, active)` policy before accessing any quest ID/state. Regression covers no-player load, first entry, repeated monster encounters, missing player pointer during camp travel, changed multiplayer player pointer, stage exit, one-time end and immediate re-entry, and training/non-Guiding stages. Repeated 109-stage updates do not restart a session or reset the capture clock; the first confirmed non-109 stage ends it. **Actual stage transitions during camp travel, loading, multiplayer joins and returns still require GL01–GL08 live evidence; these unit tests do not prove the game always reports stage 109 throughout transitions.**


### Shared Guiding Lands synthetic schema-3 fixtures (source-only, NOT runtime)

`tests/fixtures/guiding-lands-solo.json` and `tests/fixtures/guiding-lands-multiplayer-gauge-only.json` are **hand-written synthetic** snapshots consumed by **both** SolidJS/web and Rust/GPUI model regression tests. They cover a positive local-hit-only session with explicit `damageScope: local-only`, and a multiplayer-labeled gauge-only zero-damage session with two fight-local monster IDs, no inferred remote damage or hunter rows, and all optional gauges `local-unverified`. The solo fixture's numeric `percent: 100` reflects internal normalization only: viewer UIs must display unknown party share instead. Neither JSON is captured from a real hunt and neither verifies multiplayer authority, monster offsets, Windows/Proton in-game timing or runtime compatibility. GL01–GL08 remain **NOT RUN**.


### Preserve source stage at Guiding Lands exit (source-only)

The plugin reads the new destination stage before ending a GL session on area exit. A log finalized with the destination (for example Seliana) would fail the stage-109-only Guiding Lands persistence gate even when valid damage/gauge observations existed. `GuidingLandsSession.LogStageId` now stamps a GL session's log with its original source stage **109** while preserving normal quest stage handling, with explicit departure/acceptance regression tests. GL06 live re-entry and save checks remain **NOT RUN**.

### Source-only zero-total writer regression

The production `FightLogStore.EndHunt` is now compiled directly in PluginRegression. A gauge-only Guiding Lands session with a valid zero-damage local roster is serialized through the real atomic JSON/file/index paths without producing NaN, followed by an ordinary quest in the same process and reload of both files. A second gauge-only log with no local hunter row also verifies the missing-data case. CI is **source-only**, not runtime proof for GL01–GL08.

### Guiding Lands local-slot source regression

When a valid live roster matches the local hunter pointer in multiplayer, the production Guiding Lands local-only snapshot now retains that hunter's **actual slot** (e.g., guest slot 2) instead of always using slot 0. This preserves local hit/event/cart slot IDs, per-slot sampling and avoids writing the guest's observed damage into a teammate's slot. When the roster is unavailable, slot 0 is explicitly a fallback, not verified multiplayer identity. Regression exercises a nonzero guest slot, correct slot-damage array and no corruption of slot 0. GL03 host/guest source identity still requires live evidence.

### Extended Guiding Lands damage-history retention (source-only)

The bounded 900-point party damage history no longer stops after roughly 30 minutes. Once full, the production writer retains every second existing point and doubles the sampling interval (2→4→8 seconds as needed), keeping `t=0`, chronological sample timestamps and coverage through the end of a multi-hour expedition. Optional additive `damageSamplesCompactions` identifies lost resolution, not lost time coverage. Production regression writes/reads a three-hour Guiding Lands session and checks bounded sample count, monotonic time, tail coverage and metadata. Sparse observed samples are not proof of combat events between timestamps; runtime GL08 still required.
