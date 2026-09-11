# Quest-loading action-name crash

On September 11, 2026 at 15:23 Bahrain time, build `0.5.0+2026-09-11 10:12Z`
terminated while entering quest 153, Fungal Flexin' in the Ancient Forest.
The final diagnostic snapshot was three seconds into the quest, with zero damage
hook calls and zero part tags. Proton's `steam-582010.log` recorded:

```text
System.AccessViolationException
MhwDpsMeter.Plugin.ResolveEntityActionName
MhwDpsMeter.Plugin.NoteDeathAction
MhwDpsMeter.Plugin.OnEntityAction
SharpPluginLoader.Core.Actions.ActionController.DoActionHookFunc
```

The faulting read was at `0x3251`, matching an invalid action pointer `0x3231`
plus the action-name pointer offset `0x20`. A managed catch cannot recover from
this native access violation.

`OnEntityAction` covers general action-controller owners, including non-hunter
objects during loading. `PartyActionTracker.OnAction` rejected non-hunters, but
the separate cart-detection call still resolved their names before checking for
a party slot. The old resolver used unchecked native wrapper reads.

The fix checks the party slot first, then routes all local and teammate action
names through `HunterActionNames`. It verifies hunter identity, action set 0–3,
list count, index and each pointer. Names must terminate within 256 bytes.
All these reads and the hunter-vtable check use `ReadProcessMemory` with an exact
byte-count check, so stale or inaccessible memory returns unknown without a raw
managed dereference. The part-collision hook is independent and remains enabled.

The layout was checked against the installed SharpPluginLoader.Core 0.0.7.2 IL:

| Field | Layout |
| --- | --- |
| Entity action controller | Inline at entity + `0x61C8` |
| Action list | Controller + `0x68` + set × `0x10` |
| List array / count | Pointer at `+0`, int at `+8` |
| Action entry | Array + index × `8` |
| Name | String pointer at action + `0x20` |

`tests/PluginRegression` covers the original bad pointer, rejected owners, valid
death/weapon names, corrupt counts, invalid indices and missing or unterminated
names. `tests/NativeMemoryRegression` runs the production readers on Windows or
Proton against committed, inaccessible, boundary-crossing and freed memory.
The September 11 fix passed 106 simulated checks and 16 native checks under the
game's CachyOS Proton/.NET 8.0.12 runtime. An actual quest-entry retry is still
required to confirm the user-visible crash is resolved.
