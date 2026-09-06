import names from '../../../data/game-names.json'

const weapons = new Map(Object.values(names.weapons).map((entry) => [entry.key, entry.name]))
const stages: Record<string, { key: string; name: string }> = names.stages

/** Playable class keys that have a matching `data/weapon-icons/{key}.svg`. */
const WEAPON_ICONS = new Set(
  Object.values(names.weapons)
    .map((entry) => entry.key)
    .filter((key) => key !== 'None'),
)

/** Presentation only: keep the original weapon keys for move lookup and trial grouping. */
export function weaponDisplayName(key: string | null | undefined): string {
  return key ? weapons.get(key) ?? key : 'Unknown weapon'
}

/** Relative URL for a class icon, or null when the key is missing/unknown. */
export function weaponIconUrl(key: string | null | undefined): string | null {
  if (!key || !WEAPON_ICONS.has(key)) return null
  return `${import.meta.env.BASE_URL}weapon-icons/${key}.svg`
}

export function stageDisplayName(id: number, recorded?: string | null): string {
  const entry = stages[String(id)]
  // Preserve localized/custom names; replace only missing values or raw enum labels.
  if (recorded?.trim() && recorded !== entry?.key && recorded !== String(id)) return recorded
  return entry?.name ?? `Stage ${id}`
}
