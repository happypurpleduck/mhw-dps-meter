# Monster part damage: capture scope

Part-level damage categorization is implemented for **local exact hits** only.
Existing fight logs without `part` / `partName` on hit rows cannot be backfilled.

## What is recorded

Around each `DealDamage` call the plugin snapshots the monster part-health array
(HunterPie layout: monster + `0x1D058`, flinch/break meters) and tags the hit with
the part whose meter dropped (or whose flinch counter rose). Names come from
`data/monster-parts.json` (derived from HunterPie's Apache-2.0 `MonsterData.xml`).

Fields on `hits[]`:

- `part` — part index (game flinch/break slot id)
- `partName` — display label when the monster type is in the table

## What you cannot get

The game only runs deal-damage for hits simulated on this client. Teammate rows are
award-table deltas and have **no part**. The quest-award table has no part dimension.

So viewers can show:

- Your damage by part (exact)
- A “most damage” hunter per part based on tagged hits (usually only you)
- Untagged / teammate damage under **Unknown part**

They cannot honestly answer “who in the party broke the head” without a new
game-side signal that attributes remote damage to a part. Do not invent teammate
part damage from move names, tenderize flags, or flinch events.
