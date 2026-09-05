/**
 * TypeScript mirror of the plugin's FightLog.cs (schema 2).
 *
 * Every schema-1 field keeps its name, so files written by plugin 0.3.0 parse
 * with the same types; `normalizeLog` fills the newer arrays with defaults.
 */

export type LogKind = 'quest' | 'trial'

export interface FightLog {
  schemaVersion: number
  kind: LogKind
  pluginVersion?: string
  gameBuild: number
  questId: number
  questName: string
  result: string
  stageId: number
  stage?: string
  startedAt: string
  endedAt?: string
  durationSeconds: number
  /** "quest" | "local" | "trial" */
  timerSource?: string
  /** Always "local": hits[] only cover the local hunter. */
  hitCoverage?: string
  players: FightLogPlayer[]
  monsters: FightLogMonster[]
  samples: FightLogSample[]
  hits: FightLogHit[]
  events: FightLogEvent[]
}

export interface FightLogPlayer {
  slot: number
  name: string
  isLocal: boolean
  weapon?: string | null
  damage: number
  dps: number
  percent: number
}

export interface FightLogMonster {
  id: string
  type: number
  name: string
  variant: number
  maxHealth: number
  lastHealth: number
  firstSeenT: number
  diedT?: number | null
}

export interface FightLogSample {
  t: number
  /** Cumulative party damage per slot (length 4). */
  damage: number[]
}

export interface FightLogHit {
  t: number
  slot: number
  monster?: string | null
  damage: number
  crit: boolean
  tenderized: boolean
  attackId: number
  actionSet: number
  actionId: number
  /** Internal action name, e.g. "WP_02::RANBU". */
  action?: string | null
}

export type EventType = 'enrage' | 'unenrage' | 'death' | 'flinch' | 'weapon' | 'join' | 'leave' | (string & {})

export interface FightLogEvent {
  t: number
  type: EventType
  slot?: number | null
  monster?: string | null
  detail?: string | null
}

/** One row of logs/index.json. */
export interface IndexEntry {
  file: string
  kind: LogKind
  questId: number
  questName: string
  result: string
  startedAt: string
  durationSeconds: number
  totalDamage: number
  weapon?: string | null
  players: string[]
  monsters: string[]
}

export const PARTY_SLOTS = 4

/** In-game party HUD colours by slot, matching the overlay. */
export const SLOT_COLORS = ['#ff9e2e', '#61db6b', '#52b8ff', '#f26bb8'] as const

export function slotColor(slot: number): string {
  return SLOT_COLORS[Math.max(0, Math.min(SLOT_COLORS.length - 1, slot))] ?? SLOT_COLORS[0]
}
