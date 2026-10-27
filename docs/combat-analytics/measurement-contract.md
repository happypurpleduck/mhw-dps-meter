# Combat analytics — measurement and data contract

This document fixes **meaning** before calculations and UI. Implementation status is tracked in [README](README.md) / [#14](https://github.com/happypurpleduck/mhw-dps-meter/issues/14); exact per-hit application is a research question in [#15](https://github.com/happypurpleduck/mhw-dps-meter/issues/15).

## Entity identity and time

- Plugin monster pointers (`nint`) are **transient handles**, not durable IDs. Relate snapshots/hits/events to `HuntRecorder` fight-scoped identifiers (`m1`, `m2`, …), accounting for respawn/pointer reuse and quest lifecycle.
- `t` means hunt-clock seconds, with source recorded. Exact hook hits use timestamp adjusted to quest clock; periodic polls have **observation time** only. A 2s interval can contain multiple procs/resets; do not call its endpoints exact occurrence times.
- Weapon time trials in training are separate from quest-based monster state; do not fabricate monster gauges for training pole/wagon.
- A monster with no readable part data is not a monster with all part gauges equal to zero.

## Independent measurement families

| Family | Defined value | Independent properties and caveats |
| --- | --- | --- |
| Monster HP | Current/max HP; lost HP used in solo fallback | Same unit as HP damage, not status meter |
| Local hit HP damage by touched part | Displayed damage number and resolved collision normal-part id | Existing local exact hits only; **not** break buildup |
| Normal-part flinch/break | Remaining/max flinch gauge, completed flinch cycles and next break threshold per canonical part | Some breaks have several cycles; resets are not necessarily breaks |
| Sever | Separate severable meter, its max, progress and sever event | Distinct slot namespace / damage-type rules; not inferable from normal part |
| Stamina | Monster current/max stamina pool | Different from **Exhaust buildup** ailment |
| KO/stun | Ailment id 7 buildup/max, proc count, duration if applicable | KO depends on hit properties/location and decay; no per-hunter contribution from polling |
| Exhaust | Ailment id 6 buildup/max, proc count, effect state | Not current monster stamina |
| Other ailments | Per-id current/max buildup, counter, max/current duration | Poison 1, Paralysis 2, Sleep 3, Blast 4, Mount 5, etc.; only validated ids |
| Enrage | Monster-specific status/callback; buildup/timer where meaningful | Callback event vs polled status must be distinguished |
| Tenderization | Part-associated region IDs and duration/max per region | Tenderized flag on a *hit* means something different from live duration |
| Break/stagger/KO proc events | Observed transitions / native verified callbacks, source quality and clock | May need precise hook for exact event timing |
| Player contribution to non-HP meter | **Unknown until verified application hook/owner** | Never derive by subtracting health deltas or matching nearby animation |

**Terminology:** use “remaining” for a gauge value that decreases toward zero, “buildup” for one that increases toward a threshold, “counter” for number of completed procs/cycles, and “threshold” for trigger level. Do not display all meters as percentages with the same directional meaning.

## Next break / remaining cycles

Read canonical normal part metadata from `data/monster-parts.json`, preserving its original MonsterData.xml order while excluding severable entries when mapping collision normal slot ordinals. The draft stores `breakThresholds` and `tenderizeIds` for all 652 entries across 72 supported monster definitions.

For a breakable normal part with observed flinch counter `c`, proposed `nextBreakThreshold` is the first defined threshold strictly greater than `c`. If none exists, return unknown/not-applicable based on game semantics **after validation**. Current gauge `remaining/max` and this cycle count are **not HP damage**. Multi-stage behavior, resets and counter semantics must be verified on real monsters, particularly with Partbreaker.

## Provenance, reliability and missingness

For each recording, preserve:
- `origin`: direct local memory, local exact hit callback, SPL monster callback, aggregate award increment, optional remote observation.
- `authority`: **unverified** (default), validated-solo, validated-host, known-non-authoritative guest, unknown/stale, optionally synchronized-with-provenance. These are *design states*, not all implemented today.
- `precision`: exact hook timestamp, observed-at-poll, estimated interval; do not conflate.
- `availability`: present, missing unreadable, unsupported build, unimplemented/not-applicable, stale after gap. A physical zero is a distinct present value.

Existing **implemented schema-3 draft** uses `source: "local-unverified"` on optional `gaugeSamples`. It DOES NOT yet have the full provenance/coverage taxonomy above, verified role detection, or exact sampled gap annotation. Those fields are future extensions and must remain optional for old logs.

## Schema 3 — CURRENT draft contract

~~~json
{
  "schemaVersion": 3,
  "gameBuild": 421810,
  "monsters": [{"id": "m1", "type": 0, "name": "Anjanath"}],
  "hits": [
    {"t": 8.1, "slot": 0, "monster": "m1", "part": 2, "damage": 105, "crit": true}
  ],
  "gaugeSamples": [
    {
      "t": 8.0,
      "monster": "m1",
      "source": "local-unverified",
      "stamina": 90,
      "maxStamina": 150,
      "parts": [
        {"part": 2, "name": "Head", "remaining": 80, "max": 300,
         "completedFlinches": 1, "nextBreakThreshold": 5}
      ],
      "ailments": [
        {"id": 7, "name": "KO / Stun", "buildup": 75,
         "maxBuildup": 100, "procs": 2, "duration": 0, "maxDuration": 10}
      ]
    }
  ]
}
~~~

**Illustration, not a validated real hunt.** It omits other required log fields for brevity. `hits[].damage` and `gaugeSamples[].parts[].remaining` are *distinct*, even if the same part id appears. The draft C# writer records optional gauge samples approximately every 2 seconds per monster, capped at 3,600 samples per hunt globally. Old schema-1/2 files parse with an empty `gaugeSamples` array in both viewers. **Support to parse is not support to chart.** Validation #17 precedes interpreting these recorded numbers as dependable.

## Future compatible events

Any new event should carry:
- monster fight id; canonical part/status id if relevant; `t`; event type; previous/current counter or validated associated value where available.
- capture precision (`exact-hook`, `observed-at-poll`), origin/authority, and whether transition was missed due to gap.
- no inferred hunter `slot` unless game attribution independently verified.
- deduplication for SPL callbacks vs polled transition, and careful handling of simultaneous hits/procs.

Proposed kinds include `part-flinch`, `part-break`, `sever`, `ko-proc`, `ailment-proc`, `enrage`/`unenrage`, `tenderize-start`/`tenderize-end`, `gauge-read-gap`. Names are **not yet contractual or implemented**.

## Display/aggregation invariants

1. Party damage totals come from the award table; don't recompute from status/part gauges.
2. Damage-by-contact-part charts use only attributable hit HP amounts; unassigned/multi-monster teammate award deltas retain Unknown/Unassigned sections.
3. Do not backfill old fights with modern snapshots or metadata as if it were observed.
4. Don't assign status procs to hunter/move from action animation/time proximity.
5. Per-hunt status summaries are separate from HP DPS/shares; reset counters per monster generation and quest.
6. Histories must show intervals with missing reads and unstable guest authority explicitly.
7. Two viewers should agree on sample source, gauge direction, units, precision and missing-data semantics.
