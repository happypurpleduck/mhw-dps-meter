/**
 * Display names for the game's internal action names, keyed by weapon type
 * (SharpPluginLoader WeaponType names). Only confident mappings are listed;
 * everything else is prettified from the internal name, so extend this table
 * as you verify moves in your own logs.
 */
export const MOVE_NAMES: Record<string, Record<string, string>> = {
  DualBlades: {
    'WP_02::RANBU': 'Blade Dance',
    'WP_02::KIJIN_RUSH': 'Demon Flurry Rush',
    'WP_02::TWICE_SLASH': 'Double Slash',
    'WP_02::KIJIN_CHAIN': 'Demon Mode chain',
    'WP_02::EM_CONST_SPIN': 'Clutch Claw spin',
    'WP_02::EM_CONST_SPIN_FINISH': 'Clutch Claw spin finisher',
    'WP_02::CLAW_EM_STICK_ATTACK': 'Clutch Claw weapon attack',
    'WP_02::CLAW_EM_STICK_ATTACK_LAND': 'Clutch Claw weapon attack (landing)',
    'WP_02::AIR_SPIN': 'Aerial spin',
  },
  Common: {
    'Common::CLAW_EM_STICK_EM_CTRL_DIR_ADJUST_PUSH': 'Clutch Claw slinger burst',
    'Common::CLAW_EM_STICK_START_L': 'Clutch Claw grab',
  },
}

/** Human-readable fallback: "WP_02::KIJIN_SLIDING_ON" -> "Kijin Sliding On". */
export function prettifyAction(key: string): string {
  const sep = key.lastIndexOf('::')
  const tail = sep >= 0 ? key.slice(sep + 2) : key
  return tail
    .toLowerCase()
    .split('_')
    .filter(Boolean)
    .map((w) => w[0]!.toUpperCase() + w.slice(1))
    .join(' ')
}

export function moveDisplayName(weapon: string | null | undefined, key: string): string {
  const byWeapon = weapon ? MOVE_NAMES[weapon]?.[key] : undefined
  return byWeapon ?? MOVE_NAMES.Common?.[key] ?? prettifyAction(key)
}
