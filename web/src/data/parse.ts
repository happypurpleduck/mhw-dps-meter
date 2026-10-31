import type { FightLog, FightLogGaugeSample, FightLogPlayer, IndexEntry } from '../schema/fightlog'
import { gaugeI32, isGaugeI32 } from './gaugeInteger'

/** A gauge-only expedition lacks measured local hook damage. */
export const hasObservedLocalDamage = (log: FightLog): boolean =>
  log.damageScope !== 'local-only' || log.players.some(p => p.isLocal)

/** Accepts schema-1 or schema-2 JSON and returns a fully populated FightLog. */
export function normalizeLog(raw: unknown): FightLog {
  if (!raw || typeof raw !== 'object') throw new Error('fight log is not an object')
  const r = raw as Record<string, unknown>
  if (typeof r.questName !== 'string' || !Array.isArray(r.players)) {
    throw new Error('not a fight log (missing questName/players)')
  }

  const players: FightLogPlayer[] = (r.players as Record<string, unknown>[]).map((p, i) => ({
    slot: num(p.slot, i),
    name: str(p.name, `Hunter ${i + 1}`),
    isLocal: bool(p.isLocal, false),
    weapon: (p.weapon as string | null | undefined) ?? null,
    damage: num(p.damage, 0),
    dps: num(p.dps, 0),
    percent: num(p.percent, 0),
  }))
  // Schema 1 never marked the local hunter; a solo log is necessarily you.
  if (!players.some((p) => p.isLocal) && players.length === 1 && players[0]) players[0].isLocal = true

  return {
    schemaVersion: num(r.schemaVersion, 1),
    kind: r.kind === 'trial' ? 'trial' : 'quest',
    pluginVersion: (r.pluginVersion as string | undefined) ?? undefined,
    gameBuild: num(r.gameBuild, 0),
    damageScope: r.damageScope === 'local-only' ? 'local-only' : undefined,
    questId: num(r.questId, 0),
    questName: r.questName,
    result: str(r.result, ''),
    stageId: num(r.stageId, 0),
    stage: (r.stage as string | undefined) ?? undefined,
    startedAt: str(r.startedAt, ''),
    endedAt: (r.endedAt as string | undefined) ?? undefined,
    durationSeconds: num(r.durationSeconds, 0),
    timerSource: (r.timerSource as string | undefined) ?? undefined,
    hitCoverage: (r.hitCoverage as string | undefined) ?? undefined,
    rewards: (r.rewards as FightLog['rewards']) ?? null,
    players,
    monsters: Array.isArray(r.monsters) ? (r.monsters as FightLog['monsters']) : [],
    samples: Array.isArray(r.samples) ? (r.samples as FightLog['samples']) : [],
    hits: Array.isArray(r.hits) ? (r.hits as FightLog['hits']) : [],
    gaugeSamples: normalizeGaugeSamples(r.gaugeSamples),
    gaugeSamplesDropped: Math.max(0, num(r.gaugeSamplesDropped, 0)),
    gaugeClockDiscontinuities: Math.max(0, num(r.gaugeClockDiscontinuities, 0)),
    events: Array.isArray(r.events) ? (r.events as FightLog['events']) : [],
  }
}


/**
 * Optional gauge captures may come from imported or older schema-3 files.
 * Validate their nested arrays before returning typed data to either chart or
 * history selector. Corrupt observations are unavailable, never zero readings.
 */
function normalizeGaugeSamples(raw: unknown): FightLogGaugeSample[] {
  if (!Array.isArray(raw)) return []
  const samples: FightLogGaugeSample[] = []
  for (const value of raw) {
    if (!record(value) || typeof value.monster !== 'string' || !value.monster) continue
    // Keep an unplottable per-monster marker when a timestamp is corrupt.
    // Deleting it could falsely join valid samples across a clock boundary.
    const validTime = finite(value.t) && value.t >= 0
    const t = validTime ? value.t as number : -1

    const parts = (validTime && Array.isArray(value.parts) ? value.parts : [])
      .filter((p): p is Record<string, unknown> & { part: number; name: string; remaining: number; max: number } =>
        record(p) && isGaugeI32(p.part) && typeof p.name === 'string'
        && finite(p.remaining) && finite(p.max))
      .map(p => ({
        part: p.part, name: p.name, remaining: p.remaining, max: p.max,
        completedFlinches: gaugeI32(p.completedFlinches),
        nextBreakThreshold: gaugeI32(p.nextBreakThreshold),
      }))
    const ailments = (validTime && Array.isArray(value.ailments) ? value.ailments : [])
      .filter((a): a is Record<string, unknown> & { id: number; name: string } =>
        record(a) && isGaugeI32(a.id) && typeof a.name === 'string')
      // Preserve independent fields: a missing buildup does not erase a valid
      // duration and a missing duration can never be interpreted as zero.
      .map(a => ({
        id: a.id, name: a.name,
        buildup: finite(a.buildup) ? a.buildup : null,
        maxBuildup: finite(a.maxBuildup) ? a.maxBuildup : null,
        procs: gaugeI32(a.procs),
        duration: finite(a.duration) ? a.duration : null,
        maxDuration: finite(a.maxDuration) ? a.maxDuration : null,
      }))

    samples.push({
      t,
      monster: value.monster,
      source: str(value.source, 'unknown'),
      rawHuntT: finite(value.rawHuntT) ? value.rawHuntT : null,
      clockAdjusted: value.clockAdjusted === true,
      clockBoundary: typeof value.clockBoundary === 'boolean' ? value.clockBoundary : undefined,
      stamina: validTime && finite(value.stamina) ? value.stamina : null,
      maxStamina: validTime && finite(value.maxStamina) ? value.maxStamina : null,
      parts,
      ailments,
    })
  }
  return samples
}

function record(v: unknown): v is Record<string, unknown> {
  return v !== null && typeof v === 'object' && !Array.isArray(v)
}

function finite(v: unknown): v is number {
  return typeof v === 'number' && Number.isFinite(v)
}

export function parseLog(text: string): FightLog {
  return normalizeLog(JSON.parse(text))
}

/** Same derivation as FightLogIndexEntry.From in the plugin. */
export function indexEntryFromLog(log: FightLog, file: string): IndexEntry {
  return {
    file,
    kind: log.kind,
    questId: log.questId,
    questName: log.questName,
    result: log.result,
    startedAt: log.startedAt,
    durationSeconds: log.durationSeconds,
    totalDamage: log.players.reduce((sum, p) => sum + p.damage, 0),
    damageScope: log.damageScope,
    weapon: log.players.find((p) => p.isLocal)?.weapon ?? null,
    players: log.players.map((p) => p.name),
    monsters: [...new Set(log.monsters.map((m) => m.name))],
  }
}

export function parseIndex(text: string): IndexEntry[] {
  const raw = JSON.parse(text)
  if (!Array.isArray(raw)) throw new Error('index.json is not an array')
  return raw.map((e: Record<string, unknown>) => ({
    file: str(e.file, ''),
    kind: e.kind === 'trial' ? 'trial' : 'quest',
    questId: num(e.questId, 0),
    questName: str(e.questName, ''),
    result: str(e.result, ''),
    startedAt: str(e.startedAt, ''),
    durationSeconds: num(e.durationSeconds, 0),
    totalDamage: num(e.totalDamage, 0),
    damageScope: e.damageScope === 'local-only' ? 'local-only' : undefined,
    weapon: (e.weapon as string | null | undefined) ?? null,
    players: Array.isArray(e.players) ? (e.players as string[]) : [],
    monsters: Array.isArray(e.monsters) ? (e.monsters as string[]) : [],
  }))
}

export function isLogFileName(name: string): boolean {
  return name.endsWith('.json') && name !== 'index.json' && !name.startsWith('live-debug')
}

function num(v: unknown, fallback: number): number {
  return typeof v === 'number' && Number.isFinite(v) ? v : fallback
}
function str(v: unknown, fallback: string): string {
  return typeof v === 'string' ? v : fallback
}
function bool(v: unknown, fallback: boolean): boolean {
  return typeof v === 'boolean' ? v : fallback
}
