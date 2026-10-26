# Combat analytics — specification, implementation status, and tracking

**Last audited:** 2026-10-26. **Game:** Monster Hunter: World — Iceborne, Steam build 421810 first. **Project:** [mhw-dps-meter](https://github.com/happypurpleduck/mhw-dps-meter). This is the canonical project plan, not evidence of shipped functionality.

## Objective

Evolve the SharpPluginLoader C# DPS overlay and its SolidJS/GPUI fight viewers into **provenance-aware** monster-state and part-mechanics analytics. Record stamina; KO/stun, exhaust and other abnormal status buildup; flinch, break and sever thresholds; tenderization, enrage, activation and event history. Retain the useful exact local **HP damage by collision part** metric; investigate exact *buildup applied per hit* separately. Preserve Windows, Linux/Proton, offline use, training/time trials, party DPS, and old logs.

## GitHub source of truth

| Record | Scope | State |
| --- | --- | --- |
| [#14](https://github.com/happypurpleduck/mhw-dps-meter/issues/14) | Parent feature initiative and acceptance criteria | Open |
| [#15](https://github.com/happypurpleduck/mhw-dps-meter/issues/15) | **Research-only**: exact per-hit part/KO/exhaust/ailment contribution, source identity, potential sync | Open; not established |
| [#16](https://github.com/happypurpleduck/mhw-dps-meter/pull/16) | Experimental reader, derived part metadata, opt-in text preview, schema-3 samples/parsers | **Draft, not merged** |
| [#17](https://github.com/happypurpleduck/mhw-dps-meter/issues/17) | Live binary/offset verification, host/guest authority, Windows/Proton safety | Open; release gate |
| [#18](https://github.com/happypurpleduck/mhw-dps-meter/issues/18) | Sever, tenderize, enrage, full ailment proc and part-break event capture | Open |
| [#19](https://github.com/happypurpleduck/mhw-dps-meter/issues/19) | Historical state trends and timeline visualization in **both** viewers | Open |
| [#20](https://github.com/happypurpleduck/mhw-dps-meter/issues/20) | Configurable low-overhead combat overlay UX | Open |

The existing docs [part-damage](../part-damage.md), [item-damage](../item-damage.md), [monster-gauges](https://github.com/happypurpleduck/mhw-dps-meter/blob/feat/monster-state-foundation/docs/monster-gauges.md) (on draft #16), plus [measurement contract](measurement-contract.md), [verification](verification.md), and [references/agent handoff](references-and-handoff.md) are complementary.

## Baseline vs proposed capabilities

| Metric | master before #16 | #16 draft | Required remaining validation/work |
| --- | --- | --- | --- |
| Party damage/DPS | Quest-award total + local fallback | unchanged | no regressions |
| Local precise hit damage | Yes: damage number, action, crit/tenderized, monster | unchanged | treat as **HP damage only** |
| Collision part of local hit | Yes, 421810 only with verified context hook | unchanged | no retroactive attribution; 421631 unmapped |
| Teammate part/attack detail | Estimated award deltas only; no exact remote part | unchanged | #15 if new source found |
| Monster HP | Yes | unchanged | monster identity/pointer generation correctness |
| Stamina | No | local experimental, opt-in | #17 |
| KO/Exhaust/Poison/etc status buildup | No | local experimental ailments snapshot | #17 + #18 |
| Normal-part flinch/break gauges | No | local experimental gauge + next break threshold metadata | #17 + #18 |
| Sever gauges, tenderize timers, enrage meter | No | not implemented | #18 |
| Exact proc/break event timing | Enrage/death/flinch callbacks; not all gauge events | samples only | #18, #19 |
| Status/part historical trends | No | optional schema-3 gaugeSamples recorded; TS/Rust parsers accept | #19 charts and events |
| User-facing monster state panels | No | minimal unverified preview, OFF by default | #20 |
| Per-hit buildup amount by hunter | No | No | research #15, no assumptions |
| Multiplayer host-synced authoritative gauges | No | No | OPTIONAL, only after protocol/consent and authority investigation |

**Draft #16 state as of audit:** head d25e015db729bfbe6e86b7fb97ffea6701cf075a. CI run [40233131193](https://github.com/happypurpleduck/mhw-dps-meter/actions/runs/40233131193) passed plugin Linux/Windows, GPUI viewer Linux/Windows, release tooling. CI only proves build/tests, **not offsets, correct gauge semantics, gameplay safety, or host authority**. Later commits require fresh CI.

## Engineering principles / decisions

1. **Accuracy first:** HP damage to a touched part != break/flinch meter damage != KO/Exhaust/status buildup. Never sum across fundamentally different units.
2. **Do not invent ownership:** a change to a monster's gauge does not identify the attacking hunter; team award increments cannot recover part/status contribution. Retain Unknown and Unassigned damage.
3. **Provenance per reading:** local-unverified, verified host, missing, unsupported, stale, externally synchronized (future); never show unsupported numeric zero as a real gauge.
4. **Read-only and fail closed:** safe/bounded memory access; version/build gate and structure validation; never modify game state or risk a quest crash to collect metrics.
5. **Host/guest is unresolved until tested:** don't assume that client-visible ailment or part gauges are host-authoritative. Don't use misleading `exact` labels.
6. **Progressive disclosure:** default party DPS stays compact. Optional monster/parts/status UI, settings persisted; no dependency on an extra process/network.
7. **Backward compatibility:** schema v1/v2 logs still load; older fights have no invented gauge data; schema version and source preserved.
8. **Viewer parity:** SolidJS web and Rust GPUI native/browser must interpret the same data, event times and coverage.
9. **Evidence over confidence:** simulated-memory tests establish reader arithmetic only, never binary compatibility. Live captures are required for release.
10. **Avoid scope lock:** phase #15 (exact per-hit buildup/reverse engineering) need not block validated monster-state display.

## Recommended implementation order

1. Resolve and document #17 on an *opt-in* build; correct layout/read issues. **Do not merge or ship unverified feature as authoritative.**
2. Expand runtime field coverage and event capture in #18; keep negative/ambiguous observations explicitly unknown.
3. In parallel after schema stabilizes, implement #19 data analysis and compatible viewer UI; then #20 polished configurable live overlay.
4. Only after evidence, consider #15 game damage-application investigation, optional consent-based host sync, and exotic boss-specific mechanics.
5. Land independent small PRs with exact regression and in-game evidence; link PRs to the relevant issue. Merge docs-only independent of experimental runtime risk if reviewed.

## Completion / release gate

Feature #14 is not complete until **verified live readers**, validated guest/host authority behavior, separate sever/tenderize/enrage and required status/part events, useful panels, *both* viewers with meaningful charts, backward compatibility, test/CI status, Windows+Proton smoke, and documentation/evidence. PR #16 is an experimental foundation, **not** a full implementation.

## Open research and optional future scope

- Attack-to-meter application hooks; physical vs elemental split; item and environmental damage source; Palico damage; duplicate hunter names and repeated monster instances.
- Monster-specific counters (Alatreon elemental topple, Safi'jiiva, Fatalis, etc.) must be separately verified; do not fit special mechanics into generic part gauges without evidence.
- Optional consent-based peer/host sync for multiplayer **only if necessary**; no mandatory remote service.
- Historical joins/quest timer mismatch and sampled gaps must never imply an exact event time.

This document changes when a PR lands or evidence changes; the issue/PR checklist is the operational ledger.
