# Monster part damage: capture scope

Part-level damage categorization is implemented for **local exact hits** only.
Existing fight logs without `part` / `partName` on hit rows cannot be backfilled.

## What is recorded

The plugin hooks the caller of `FUN_DEAL_DAMAGE` that still has the collision
context. It reads the normal part slot from that context and scopes it to the
nested damage-number callback, matching both monster and position pointers.
The context is thread-local and restored after nested calls and exceptions.
Calls without a matching context remain untagged.

Collision slots are normal-array ordinals. They are translated to the canonical
ids in `data/monster-parts.json` using the original MonsterData.xml order, skipping
severable meters. For example, Anjanath normal slot 0 is Head (canonical id 2),
not Tail (id 0). The selector also respects the game's alternate collision part
when the original part's state is 1.

Fields on `hits[]`:

- `part` — canonical part id from the HunterPie-derived table
- `partName` — display label

This categorizes the hit's **HP damage by the part hit**; it does not measure the
separate break/sever buildup amount or identify who caused a part break.

## Why the original capture recorded no parts

The September 10 Tobi-Kadachi log (`2026-09-10_233057_30000_complete.json`,
plugin `0.5.0+2026-09-10 20:22Z`) contained 852 hits and zero part tags.
The original resolver compared part meters immediately before and after
`FUN_DEAL_DAMAGE`. Inspection of the running 421810 executable showed that this
function displays damage numbers; it does not apply damage to those meters.
Its third argument is a world-position vector, so treating it as a part pointer
or a small part id was also invalid.

Verified call path in build **421810** (addresses use image base `0x140000000`):

- `0x1402C66B0` applies hit damage; `0x1402C66C6` reads the collision pointer
  from `hitData + 0x28`, `0x1402C66DE` reads its normal slot at `+0x60`, and
  `0x1402C670D..0x1402C671E` selects `collision + 0x80` when
  `controller + slot * 0x1F8 + 0x208` is 1.
- `0x1402C7368` calls `0x1402C3030` with `(controller, hitData, damageData)`.
  This caller is now hooked as `FUN_SHOW_DAMAGE_CONTEXT`.
- `0x1402C32EC..0x1402C332E` passes `controller + 0x08` as the monster,
  `hitData + 0xE0` as the position, and calls `FUN_DEAL_DAMAGE` at `0x141CC5F80`.
- `0x141CC6114..0x141CC6122` reads the position's three floats for a distance
  check, independently confirming that it is a vector.

The context hook is enabled only with a verified map entry and matching function
prologue. Currently only **421810** has that entry; **421631** still records hits
but leaves parts unknown until its context hook is verified. Failure to enable
the context hook does not disable ordinary damage recording.

## Diagnostics and verification

F9's **Part capture** line and `partCapture` in `logs/live-debug.json` report the
context hook status, tagged/unknown hit counts, and the most recent resolution.
The same information is written to `live-debug.log`. Counts reset each hunt.

Regression checks exercise the production readers and both detours with simulated
native calls: unchanged meters, slot zero, canonical names, alternate parts,
unreadable data, nested calls, exception cleanup, training, and signature refusal.
Live smoke verification on September 11 with build `0.5.0+2026-09-10 21:00Z`
recorded 34/34 Anjanath hits with part tags and no unknowns, including normal
slot 0 → Head (id 2), slot 2 → Left Leg (id 4), and slot 4 → Tail (id 6).
The hunt was still running at this checkpoint; completed-log verification can
check `part`/`partName` after the quest ends. For future builds, confirm the new
build stamp and that `tagged` increases while hitting a large monster.

## Viewer details

Both viewers offer a monster selector and a selectable row for every recorded
part, including Unknown part. The detail view shows all hunters with recorded
hits on that part, with damage, share, hunt DPS, hit/row count, average and largest
hit, and crit/tenderized rates. Known-part coverage is measured against recorded
hits on the selected monster, not the quest-award totals.

Charts are rebuilt from the selected part's hit timestamps. They show cumulative
damage, DPS in 2-second intervals (with the final partial interval divided by its
actual duration), and rolling DPS with a 10/20/30/60-second window. Idle time is
included through the end of the hunt. Hunter series are keyed by slot so equal
hunter names cannot merge their curves. No quest-award damage from other parts or
monsters is added to these charts.

The hunter table marks any contribution containing estimated rows and leaves its
crit/tenderized rates unavailable. Estimated rows are always classified as Unknown
part. Their counts and average/largest amounts describe award increments, not
individual attacks. The same distinction applies to Unknown part's aggregate.

Damage with no `monster` is shown in a separate **Unassigned damage** selection,
with all contributing hunters and the same three charts. A notice above the part
selector identifies those hunters and their recorded damage, so selecting a known
monster does not silently hide their existence. These rows never enter a known
monster's totals, shares or part charts. Logs containing only unassigned rows are
also viewable, even if the monster list is empty.

## Why other hunters have no part tags

The current collision/damage-number hooks capture local exact hits. Teammates use
a different path: `PartyDamageReader` reads their cumulative quest-award totals,
and `PartyActionTracker.Attribute` emits the increases as estimated rows. Those
increases have **no part information**. `HuntRecorder.SingleLiveMonsterId` supplies
a monster only when exactly one tracked large monster is alive. With multiple
monsters present, the teammate rows have neither a monster nor a part.

The September 11 multiplayer log `2026-09-11_034010_1401_complete.json`
(When the Mist Taketh You, plugin `0.5.0+2026-09-10 21:00Z`) demonstrates both:

| Hunter | Recorded rows | Recorded damage | Part-tagged rows | Rows without monster |
| --- | ---: | ---: | ---: | ---: |
| duck (local) | 901 | 14,427 | 901 | 0 |
| Lexareds | 301 | 8,708 | 0 | 301 |
| MAJEED | 266 | 8,460 | 0 | 266 |

The earlier Parts view filtered out every row without a monster, hiding all 567
teammate rows. Both viewers now expose their 17,168 recorded damage under
Unassigned damage. These amounts are the stored increments, and can differ from
final quest-award totals; unrecorded increments are not reconstructed.

So viewers can show:

- Your damage by part (exact when a collision part is resolved)
- All hunters with exact tagged hits on a part (normally only the local hunter)
- Untagged / teammate damage under **Unknown part** when a monster is assigned
- Damage without a monster under **Unassigned damage**, with each hunter's charts

Displaying unassigned damage does not enable remote part capture. Accurate
teammate part attribution needs a new verified game-side signal or exact logs
from those hunters; the existing totals cannot recover which parts they hit.
Do not invent teammate part damage from move names, tenderize flags, or flinch
events. This is a limit of the current capture sources, not proof that another
game-side source is impossible.
