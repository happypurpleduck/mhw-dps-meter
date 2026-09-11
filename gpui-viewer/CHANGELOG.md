# MHW Fight Logs

## 0.3.0

### Minor Changes

- e07e70e: Expand the Parts view with selectable monster parts, full hunter contributions,
  hit statistics, cumulative damage, interval DPS, and configurable rolling DPS.
  Build each part's curves from recorded hits, including idle periods and final
  partial intervals, while keeping untagged and estimated damage under Unknown part.
  Expose rows without a monster separately under Unassigned damage, with all hunter
  contributions and charts. Make missing teammate part capture explicit and keep
  unassigned damage out of known-monster totals.

## 0.2.2

### Patch Changes

- 13c9fe8: Tag local exact hits with the monster part from the part-health meters and show damage by part in both viewers, with part names from the new monster-parts table.

## 0.2.1

### Patch Changes

- 5dcab16: Record hunter carts from the quest death counter in fight logs and the overlay, with timeline markers and carts columns in both viewers.

## 0.2.0

### Minor Changes

- 185fffe: Watch native log folders for new and changed fights while preserving the selected fight and comparisons. Add a DPS chart using consecutive sample intervals with no rolling window.
  
  Reject ambiguous teammate entity matches, discard despawned candidates, identify single-teammate weapons without an award table, and correct the older loader's reversed bowgun names for new recordings.

### Patch Changes

- afce9ab: Add automated Linux and Windows builds, versioned release archives, and SHA-256 checksums.

Changes from the automated release pipeline are recorded here. Earlier releases predate this changelog.
