# Game-name mapping audit

`game-names.json` is shared by the plugin and both viewers. IDs and enum keys
were checked against the installed SharpPluginLoader.Core **0.0.7.2** assembly,
the exact version referenced by the plugin. The `name` fields are English display
labels; they do not replace weapon keys in logs, move lookup, or personal bests.

| Category | Coverage and remaining work |
| --- | --- |
| Quests | Names are read from the game's localized text through SPL. A failed current-quest lookup now still tries the supplied quest ID. Blank and unavailable results fall back to `Quest <id>`. There is no verified offline quest catalog; arena/challenge names missing from the game remain unresolved. Sample logs are examples, not authoritative ID sources. |
| Areas | All 35 SPL stage enum values, including internal/debug stages. Explicit labels cover Elder's Recess, Training Area, gathering hubs, Origin Isle and Alatreon's Secluded Valley. Unknown IDs retain their number. Internal stage labels are descriptive enum expansions, not official destination names. |
| Classes | All 14 weapon classes plus the `None` sentinel. Both viewers use display labels while retaining canonical SPL keys such as `GunLance` and `SwordAndShield`. Class icons live in `data/weapon-icons/` (MIT, OpenIconLibrary) and are shown beside hunter names instead of a separate weapon column. |
| Equipment | No equipment IDs, armor, charms, decorations, skills, augments or individual weapon names are captured in the schema. A weapon class is insufficient to infer a loadout. Add a verified equipment reader and persist raw IDs (with weapon class for weapon IDs) before adding equipment-name tables. Never derive a loadout from class or damage. |
| Monster names | All 102 SPL enum entries, including small monsters, training objects and the unavailable sentinel. The plugin prefers the game's localized name, then uses the table. Explicit fallbacks expand shortened names such as Coral Pukei-Pukei, Blackveil Vaal Hazak, Shara Ishvalda and Raging Brachydios. This does not establish new variant-ID mappings or change tracking eligibility. |
| Monster parts | `monster-parts.json` maps monster type → part id → display name. Derived from HunterPie `MonsterData.xml` (Apache-2.0); string keys are humanized (`PART_HEAD` → `Head`). Used when writing `partName` on local hits and for display fallbacks. Missing ids stay as `Part <id>`. See `docs/part-damage.md`. |
| Hunter names | Read from party data; player-created names have no static mapping. Preserve Unicode and spelling. Existing slot/entity matching remains responsible for identity. |
| Move names | Existing tables contain 9 Dual Blades actions and 2 common actions in each viewer. The other classes still use prettified internal action names. New move labels require weapon/action evidence; numeric action IDs or Japanese internal tokens alone are insufficient. These existing labels were not independently validated in-game by this audit. |

SPL's [quest implementation](https://github.com/Fexty12573/SharpPluginLoader/blob/a35e4d4b740b90661a32edbdc7e694b619e0bfa9/SharpPluginLoader.Core/Quest.cs)
confirms that `CurrentQuestName` delegates to `GetQuestName(CurrentQuestId)` and
that an absent text pointer returns an empty string. The explicit-ID retry helps
when reading the current ID fails; it cannot supply text absent from the game.

The plugin writes improved stage and fallback monster labels to new logs. Both
viewers also repair missing/raw stage labels when displaying old logs, while
preserving recorded localized/custom stage names. They do not rewrite log files.
