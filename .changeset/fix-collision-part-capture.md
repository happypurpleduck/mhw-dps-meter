---
"mhw-dps-meter": patch
---

Fix missing monster-part tags on build 421810 by reading the hit collision context
instead of comparing part meters around the damage-number callback. Translate
normal part slots to canonical names and expose capture coverage in diagnostics.
