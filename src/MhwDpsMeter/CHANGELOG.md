# MHW DPS Meter

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
