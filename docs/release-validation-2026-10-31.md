# Release validation: ordinary quests on build 421810

Manual gameplay testing on October 31, 2026 used plugin
`0.6.1+5dcaf255d6e852f401cf984912e5cb50836462f0`, MHW build **421810**,
and Linux/Proton. This is a bounded smoke test, not full acceptance of every
scenario in [the runtime matrix](combat-analytics/verification.md).

## Observed gameplay

- Baseline solo DPS and saved logs with experimental capture off; local hook
  and award totals matched in the controlled Kulu-Ya-Ku case.
- With capture enabled, changing stamina/status/normal-part observations,
  local-unverified provenance, a part-break notification and retained raw
  post-break counters. Precise gauge offsets and part/status semantics have
  no independent reader comparison.
- Manual single-monster display while all three monsters continued recording.
- Ordinary quest return/completion and one saved file/index entry per hunt;
  current hunt identities and bounded gauge histories did not inherit the
  preceding hunt's monsters.
- A solo Legiana completion and a two-hunter **hosted Odogaron capture**.
  Teammate action rows were explicitly estimated and remained unassigned to
  a monster/part when multiple monsters were present.
- The tester explicitly confirmed that active monster gauges cleared after
  returning from the hunt. No new screenshot or hub diagnostic was supplied
  for that final visual confirmation.

## Corrections included in this release

Two ordinary damage-source defects were confirmed in the installed build:

1. Unrelated monster HP loss could replace a working local hook's zero and
   credit an idle hunter, including in saved cumulative history. The source
   correction uses the working hook, including zero, while positive party
   awards are unavailable; the legacy HP estimate remains a fallback only
   when that hook is unavailable.
2. When party awards were available, a zero local award could be replaced by
   a positive hook total, then fall when the local award became positive.
   One hosted capture stored **450 at t=46 → 49 at t=48**. The correction keeps
   every slot on the synchronized award source, including zero slots.

The production merger, actual file writer and JSON readback are covered by
**662 passing plugin regression checks**. The corrected production Release
build passed with zero warnings/errors. These are **source/simulated results**:
the fixes were not installed into the running game during this test session,
and their installed-game retests remain pending. Release CI separately checks
the exact release commit on Linux and Windows.

Raw hook rows can differ from selected award totals. The captured differences
were preserved; damage size, current action and nearby sampled status counters
do not prove actor/item ownership or game credit rules. No historical records
were repaired or guessed. See [item attribution limits](item-damage.md).

The hosted **capture** also produced a monster lifecycle `death` callback with
positive last-recorded HP. That callback/`diedT` alone is not proof of a kill;
the log's `complete` result does not distinguish capture from slaying.

## Remaining validation

- **Guiding Lands gameplay testing is pending**, including continuous sessions,
  camp travel, carts, departure/re-entry and expedition saves. Its implemented
  local-only source policy is covered by source tests, not live proof.
- Numerical stamina/status/part accuracy has no independent-reader comparison.
  The tester could not confirm the observed Legiana paralysis visually;
  controlled KO testing was unavailable.
- Guest authority, late joins, pointer reuse, manual-selection reset, all F9
  toggles, stale-age recovery, training/time trials and frame/memory overhead
  have not received full runtime acceptance in this session.
- The viewer history features have automated parser/model coverage; manual
  inspection of the new real logs in the native viewer remains pending.
- Separate severable gauges, tenderize/enrage durations and per-hit buildup or
  hunter attribution remain deferred. Sampled counter changes are intervals
  between observations, not exact game-event timestamps.

Experimental gauges therefore remain **opt-in and local-unverified**. A stable
release version does not promote these readings into verified host authority.
