# MHW DPS Meter

## 0.6.2

### Patch Changes

- 00396cf: Keep every slot on synchronized quest-award totals once party awards are available, including local zero values. Mixing raw hook damage into a zero award slot could inflate the overlay and saved history, then make cumulative damage fall when that slot received its first positive award.
- 00396cf: Keep ordinary-quest local damage at the working hit hook's measured total, including zero, while award totals are unavailable. Unrelated monster HP loss no longer inflates an idle hunter's DPS; the legacy solo HP estimate remains available when the hook is unavailable.
- 00396cf: Document ordinary solo and two-hunter hosted quest smoke testing on build 421810 under Linux/Proton, including saved gauge histories, part-counter observations, manual display selection and clearing after quest exit. Numerical gauge accuracy, guest authority and Guiding Lands gameplay remain unverified. The two ordinary damage-source corrections have regression coverage but still need an installed-game retest. See the release validation notes for the tested scope and remaining limits.
- bb4a82d: Add experimental monster stamina, ailment and normal-part observations for confirmed game build 421810. Capture remains off by default; F9 provides compact/detailed rows, category switches, manual monster selection and explicit missing/stale-sample labels. Readings remain local observations, not verified host state.

  Add schema-3 sampled gauge histories in the native and web viewers, with observed counter-change intervals, missing-data gaps, capture-quality summaries and explicit recording/clock limits. Older fight logs remain readable; sampled counters do not establish exact proc or break times or hunter attribution.

  Add continuous Guiding Lands sessions with local-hook damage only, separate expedition logs and bounded long-session history. Teammate damage stays unknown. Guiding Lands gameplay testing remains pending.

## 0.6.1

### Patch Changes

- e07e70e: Fix missing monster-part tags on build 421810 by reading the hit collision context
  instead of comparing part meters around the damage-number callback. Translate
  normal part slots to canonical names and expose capture coverage in diagnostics.
- e07e70e: Fix a quest-loading crash in cart detection when a general action callback supplies
  a non-hunter owner. Require an identified party slot before inspecting cart actions,
  and validate hunters, action-list bounds, pointers and bounded names using protected
  OS memory copies. Invalid or stale action data now leaves the name unknown.

## 0.6.0

### Minor Changes

- 13c9fe8: Tag local exact hits with the monster part from the part-health meters and show damage by part in both viewers, with part names from the new monster-parts table.

## 0.5.0

### Minor Changes

- 5dcab16: Record hunter carts from the quest death counter in fight logs and the overlay, with timeline markers and carts columns in both viewers.

## 0.4.1

### Patch Changes

- afce9ab: Add automated Linux and Windows builds, versioned release archives, and SHA-256 checksums.
- 185fffe: Watch native log folders for new and changed fights while preserving the selected fight and comparisons. Add a DPS chart using consecutive sample intervals with no rolling window.

  Reject ambiguous teammate entity matches, discard despawned candidates, identify single-teammate weapons without an award table, and correct the older loader's reversed bowgun names for new recordings.

Changes from the automated release pipeline are recorded here. Earlier releases predate this changelog.
