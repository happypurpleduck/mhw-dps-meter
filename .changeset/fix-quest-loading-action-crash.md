---
"mhw-dps-meter": patch
---

Fix a quest-loading crash in cart detection when a general action callback supplies
a non-hunter owner. Require an identified party slot before inspecting cart actions,
and validate hunters, action-list bounds, pointers and bounded names using protected
OS memory copies. Invalid or stale action data now leaves the name unknown.
