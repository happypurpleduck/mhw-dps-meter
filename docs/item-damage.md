# Item damage attribution: capture blocker

Item damage separation and placer/detonator attribution are **not implemented**.
Existing fight logs cannot supply those identities retrospectively.

`DamageTracker.OnDealDamage` receives a target, damage, impact position, flags,
attack ID and unknown arguments. It receives no validated source entity, item
instance, owner, or triggering hunter. The current action describes the local
hunter when damage lands; it does not establish who placed a bomb. Teammate rows
are aggregate award deltas and can combine weapon, item and other damage.

The [SPL plugin events](https://github.com/Fexty12573/SharpPluginLoader/blob/master/SharpPluginLoader.Core/IPlugin.cs)
provide monster lifecycle, action and weapon-change callbacks, but no item placement
or detonation callback. [Player.CreateShell](https://github.com/Fexty12573/SharpPluginLoader/blob/master/SharpPluginLoader.Core/Entities/Player.cs)
passes an entity to shell creation, but that alone does not identify barrel-bomb
placement, explosion damage or the hunter who triggers the explosion. No item hook
addresses or source layouts are validated in this project's address maps.

Implementation requires game-side tracing to establish:

- An item instance and its placer at creation, with a validated hunter-to-party-slot identity.
- The activation event and its triggering source (hunter, another bomb, monster or timer).
- Each resulting monster-damage event linked to that item instance, including chain reactions.
- Whether award totals credit that damage to the placer or detonator, to avoid double counting
  when separating weapon damage from item damage.

Validate with controlled solo and multiplayer captures: self-detonation, another
hunter detonating, multiple owners' bombs chained together, timed bombs, monster
activation, and simultaneous weapon damage. Preserve unknown identities explicitly;
do not infer them from damage size, the current action, or proximity in time.

Once the source links are validated, add item events and source-tagged damage rows,
then derive separate item/weapon totals and show placer and detonator independently.
Do not subtract guessed bomb damage from existing player totals.
