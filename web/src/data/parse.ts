import type { FightLog, FightLogPlayer, IndexEntry } from '../schema/fightlog'

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
    events: Array.isArray(r.events) ? (r.events as FightLog['events']) : [],
  }
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
