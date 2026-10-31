# MHW Fight Logs

## 0.3.1

### Patch Changes

- 00396cf: Document ordinary solo and two-hunter hosted quest smoke testing on build 421810 under Linux/Proton, including saved gauge histories, part-counter observations, manual display selection and clearing after quest exit. Numerical gauge accuracy, guest authority and Guiding Lands gameplay remain unverified. The two ordinary damage-source corrections have regression coverage but still need an installed-game retest. See the release validation notes for the tested scope and remaining limits.
- bb4a82d: Add experimental monster stamina, ailment and normal-part observations for confirmed game build 421810. Capture remains off by default; F9 provides compact/detailed rows, category switches, manual monster selection and explicit missing/stale-sample labels. Readings remain local observations, not verified host state.

  Add schema-3 sampled gauge histories in the native and web viewers, with observed counter-change intervals, missing-data gaps, capture-quality summaries and explicit recording/clock limits. Older fight logs remain readable; sampled counters do not establish exact proc or break times or hunter attribution.

  Add continuous Guiding Lands sessions with local-hook damage only, separate expedition logs and bounded long-session history. Teammate damage stays unknown. Guiding Lands gameplay testing remains pending.

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
