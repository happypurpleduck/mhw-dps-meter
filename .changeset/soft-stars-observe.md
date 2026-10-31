---
"mhw-dps-meter": patch
"mhw-log-viewer": patch
---

Add experimental monster stamina, ailment and normal-part observations for confirmed game build 421810. Capture remains off by default; F9 provides compact/detailed rows, category switches, manual monster selection and explicit missing/stale-sample labels. Readings remain local observations, not verified host state.

Add schema-3 sampled gauge histories in the native and web viewers, with observed counter-change intervals, missing-data gaps, capture-quality summaries and explicit recording/clock limits. Older fight logs remain readable; sampled counters do not establish exact proc or break times or hunter attribution.

Add continuous Guiding Lands sessions with local-hook damage only, separate expedition logs and bounded long-session history. Teammate damage stays unknown. Guiding Lands gameplay testing remains pending.
