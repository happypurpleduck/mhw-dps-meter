/**
 * TypeScript mirror of the plugin's FightLog.cs (schema 3).
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
  /** Explicit source boundary: recorded local hits, remote party damage unknown. */
  damageScope?: string
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
  /** "local" (your exact hits only) or "party-estimated" (teammates have estimated rows too). */
  hitCoverage?: string
  rewards?: FightLogRewards | null
  players: FightLogPlayer[]
  monsters: FightLogMonster[]
  samples: FightLogSample[]
  /** Number of bounded damage-timeline compactions on long sessions. */
  damageSamplesCompactions?: number
  hits: FightLogHit[]
  /** Experimental local-only sampled observations (not host-authoritative). */
  gaugeSamples: FightLogGaugeSample[]
  /** Due observation intervals omitted after the gauge recording limit. */
  gaugeSamplesDropped: number
  /** Clock discontinuities: gauge sample t is capture-normalized, rawHuntT preserves quest timer. */
  gaugeClockDiscontinuities: number
  events: FightLogEvent[]
}

export interface FightLogGaugeSample {
  /** Nondecreasing time in capture order, corrected after hunt-clock discontinuities. */
  t: number
  rawHuntT?: number | null
  /** Persistent normalization offset, not a new break each sample. */
  clockAdjusted?: boolean
  /** A real clock break, latched until this monster's next recorded sample. */
  clockBoundary?: boolean | null
  monster: string
  source: string
  stamina: number | null
  maxStamina: number | null
  parts: Array<{
    part: number
    name: string
    remaining: number
    max: number
    completedFlinches: number | null
    nextBreakThreshold: number | null
  }>
  ailments: Array<{
    id: number
    name: string
    buildup: number | null
    maxBuildup: number | null
    procs: number | null
    /** Optional sampled duration, not an exact status start/end timestamp. */
    duration: number | null
    maxDuration: number | null
  }>
}

export interface FightLogPlayer {
  slot: number
  name: string
  isLocal: boolean
  weapon?: string | null
  damage: number
  dps: number
  percent: number
  /** Times this hunter carted (quest death counter, attributed when known). */
  carts?: number
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
  /** Monster part index (flinch/break slot) for local exact hits when known. */
  part?: number | null
  /** Display name for `part` when resolvable from the part table. */
  partName?: string | null
  /** Internal action name, e.g. "WP_02::RANBU". */
  action?: string | null
  /** Teammate row: award-table delta credited to the current move, not an exact hit. */
  estimated?: boolean
}

export interface FightLogRewards {
  zenny: number
  hunterRankPoints: number
  stars: number
}

export type EventType = 'enrage' | 'unenrage' | 'death' | 'flinch' | 'weapon' | 'join' | 'leave' | 'cart' | (string & {})

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
  damageScope?: string
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
