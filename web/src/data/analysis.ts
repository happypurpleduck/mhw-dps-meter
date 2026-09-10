import type { FightLog, FightLogHit, FightLogMonster, FightLogPlayer, IndexEntry } from '../schema/fightlog'
import { PARTY_SLOTS } from '../schema/fightlog'

/** One point of a per-slot cumulative damage curve. */
export interface CurvePoint {
  t: number
  damage: number
  slot: number
  player: string
}

export interface RatePoint {
  t: number
  dps: number
  slot: number
  player: string
}

export interface MoveRow {
  /** Internal action name or "action set/id" fallback. */
  key: string
  /** Display name (see moves/names). */
  name: string
  damage: number
  hits: number
  crits: number
  critRate: number
  tenderized: number
  avg: number
  max: number
  share: number
  isCommon: boolean
}

export interface MonsterRow extends FightLogMonster {
  hpLost: number
  yourDamage: number
  yourHits: number
}

export function playerBySlot(log: FightLog, slot: number): FightLogPlayer | undefined {
  return log.players.find((p) => p.slot === slot)
}

export function localPlayer(log: FightLog): FightLogPlayer | undefined {
  return log.players.find((p) => p.isLocal) ?? log.players[0]
}

export function totalDamage(log: FightLog): number {
  return log.players.reduce((sum, p) => sum + p.damage, 0)
}

/** Slots that have a player row or any non-zero sample. */
export function activeSlots(log: FightLog): number[] {
  const slots = new Set(log.players.map((p) => p.slot))
  for (const s of log.samples) s.damage.forEach((d, i) => d > 0 && slots.add(i))
  return [...slots].filter((s) => s >= 0 && s < PARTY_SLOTS).sort()
}

/** Cumulative damage per slot over time, one series per active slot. */
export function damageCurves(log: FightLog): CurvePoint[] {
  const out: CurvePoint[] = []
  for (const slot of activeSlots(log)) {
    const name = playerBySlot(log, slot)?.name ?? `Slot ${slot + 1}`
    for (const s of log.samples) out.push({ t: s.t, damage: s.damage[slot] ?? 0, slot, player: name })
  }
  return out
}

/** Damage gained / elapsed time between consecutive samples; no rolling window. */
export function intervalDps(log: FightLog): RatePoint[] {
  const out: RatePoint[] = []
  for (const slot of activeSlots(log)) {
    const player = playerBySlot(log, slot)?.name ?? `Slot ${slot + 1}`
    let previousTime = 0
    let previousDamage = 0
    for (const sample of log.samples) {
      const damage = sample.damage[slot] ?? 0
      const dt = sample.t - previousTime
      const dps = dt > 0 ? Math.max(0, damage - previousDamage) / dt : 0
      out.push({ t: sample.t, dps, slot, player })
      if (sample.t >= previousTime) {
        previousTime = sample.t
        previousDamage = damage
      }
    }
  }
  return out
}

/**
 * Rolling DPS over a window (seconds) from the cumulative samples.
 * Trials rebuild their samples from hits, so this works for both kinds.
 */
export function rollingDps(log: FightLog, windowSeconds = 20): RatePoint[] {
  const out: RatePoint[] = []
  const samples = log.samples
  for (const slot of activeSlots(log)) {
    const name = playerBySlot(log, slot)?.name ?? `Slot ${slot + 1}`
    for (let i = 0; i < samples.length; i++) {
      const cur = samples[i]!
      let j = i
      while (j > 0 && cur.t - samples[j - 1]!.t < windowSeconds) j--
      const prev = samples[j]!
      const dt = cur.t - prev.t
      const dps = dt > 0 ? ((cur.damage[slot] ?? 0) - (prev.damage[slot] ?? 0)) / dt : 0
      out.push({ t: cur.t, dps: Math.max(0, dps), slot, player: name })
    }
  }
  return out
}

/** Hits of one hunter; teammates only have estimated rows. */
export function hitsForSlot(log: FightLog, slot: number): FightLogHit[] {
  return log.hits.filter((h) => h.slot === slot)
}

/** Slots that have any hit rows, local first. */
export function slotsWithHits(log: FightLog): number[] {
  const slots = [...new Set(log.hits.map((h) => h.slot))]
  const local = localPlayer(log)?.slot
  return slots.sort((a, b) => (a === local ? -1 : b === local ? 1 : a - b))
}

export function isEstimated(hits: FightLogHit[]): boolean {
  return hits.length > 0 && hits.every((h) => h.estimated)
}

export function isCommonAction(action: string | null | undefined): boolean {
  return !!action && action.startsWith('Common::')
}

/** Per-move breakdown of the local hunter's hits, biggest damage first. */
export function moveBreakdown(hits: FightLogHit[], displayName: (key: string) => string): MoveRow[] {
  const groups = new Map<string, MoveRow>()
  let total = 0
  for (const hit of hits) {
    const key = hit.action ?? `action ${hit.actionSet}/${hit.actionId}`
    total += hit.damage
    let row = groups.get(key)
    if (!row) {
      row = {
        key,
        name: displayName(key),
        damage: 0,
        hits: 0,
        crits: 0,
        critRate: 0,
        tenderized: 0,
        avg: 0,
        max: 0,
        share: 0,
        isCommon: isCommonAction(hit.action),
      }
      groups.set(key, row)
    }
    row.damage += hit.damage
    row.hits += 1
    if (hit.crit) row.crits += 1
    if (hit.tenderized) row.tenderized += 1
    if (hit.damage > row.max) row.max = hit.damage
  }
  const rows = [...groups.values()]
  for (const row of rows) {
    row.critRate = row.hits ? row.crits / row.hits : 0
    row.avg = row.hits ? row.damage / row.hits : 0
    row.share = total ? row.damage / total : 0
  }
  return rows.sort((a, b) => b.damage - a.damage)
}

export function monsterBreakdown(log: FightLog): MonsterRow[] {
  const local = localPlayer(log)?.slot
  return log.monsters.map((m) => {
    const mine = log.hits.filter((h) => h.monster === m.id && h.slot === local && !h.estimated)
    return {
      ...m,
      hpLost: Math.max(0, m.maxHealth - m.lastHealth),
      yourDamage: mine.reduce((s, h) => s + h.damage, 0),
      yourHits: mine.length,
    }
  })
}

export interface PartHunterRow {
  slot: number
  name: string
  damage: number
  hits: number
  share: number
  /** True when this row is entirely estimated award deltas (no part tags). */
  estimated: boolean
}

export interface PartRow {
  /** Part index, or null for hits with no resolvable part. */
  part: number | null
  name: string
  damage: number
  hits: number
  share: number
  hunters: PartHunterRow[]
}

/**
 * Damage grouped by monster part. Exact local hits carry `part`; teammate rows and
 * untagged hits fall under "Unknown part". Hunter ranking within a part only reflects
 * hits that have that part tag (today: local hunter only).
 */
export function partBreakdown(log: FightLog, monsterId?: string | null): PartRow[] {
  const hits = monsterId
    ? log.hits.filter((h) => h.monster === monsterId)
    : log.hits.filter((h) => h.monster)
  const groups = new Map<string, PartRow>()
  let total = 0
  for (const hit of hits) {
    total += hit.damage
    const part = hit.part ?? null
    const key = part == null ? 'unknown' : String(part)
    let row = groups.get(key)
    if (!row) {
      row = {
        part,
        name: part == null ? 'Unknown part' : hit.partName ?? `Part ${part}`,
        damage: 0,
        hits: 0,
        share: 0,
        hunters: [],
      }
      groups.set(key, row)
    } else if (part != null && row.name.startsWith('Part ') && hit.partName) {
      row.name = hit.partName
    }
    row.damage += hit.damage
    row.hits += 1

    let hunter = row.hunters.find((h) => h.slot === hit.slot)
    if (!hunter) {
      hunter = {
        slot: hit.slot,
        name: playerBySlot(log, hit.slot)?.name ?? `Slot ${hit.slot + 1}`,
        damage: 0,
        hits: 0,
        share: 0,
        estimated: !!hit.estimated,
      }
      row.hunters.push(hunter)
    }
    hunter.damage += hit.damage
    hunter.hits += 1
    hunter.estimated = hunter.estimated && !!hit.estimated
  }

  const rows = [...groups.values()]
  for (const row of rows) {
    row.share = total ? row.damage / total : 0
    for (const hunter of row.hunters) {
      hunter.share = row.damage ? hunter.damage / row.damage : 0
    }
    row.hunters.sort((a, b) => b.damage - a.damage)
  }
  return rows.sort((a, b) => b.damage - a.damage)
}

export function hasPartData(log: FightLog): boolean {
  return log.hits.some((h) => h.part != null && !h.estimated)
}

export interface HitStats {
  hits: number
  crits: number
  critRate: number
  tenderized: number
  tenderizedRate: number
  damage: number
  avg: number
  max: number
  /** Seconds between first and last hit. */
  activeSeconds: number
  /** Damage over active seconds. */
  activeDps: number
}

export function hitStats(hits: FightLogHit[]): HitStats {
  const n = hits.length
  const crits = hits.filter((h) => h.crit).length
  const tenderized = hits.filter((h) => h.tenderized).length
  const damage = hits.reduce((s, h) => s + h.damage, 0)
  const max = hits.reduce((m, h) => Math.max(m, h.damage), 0)
  const first = hits[0]?.t ?? 0
  const last = hits[n - 1]?.t ?? 0
  const activeSeconds = Math.max(0, last - first)
  return {
    hits: n,
    crits,
    critRate: n ? crits / n : 0,
    tenderized,
    tenderizedRate: n ? tenderized / n : 0,
    damage,
    avg: n ? damage / n : 0,
    max,
    activeSeconds,
    activeDps: activeSeconds > 0 ? damage / activeSeconds : 0,
  }
}

export interface TrialBest {
  weapon: string
  durationSeconds: number
  best: IndexEntry
  attempts: number
}

/** Personal bests per weapon and duration, as the plugin's F9 panel computes them. */
export function trialBests(entries: IndexEntry[]): TrialBest[] {
  const groups = new Map<string, TrialBest>()
  for (const e of entries) {
    if (e.kind !== 'trial') continue
    const weapon = e.weapon ?? '?'
    const dur = Math.round(e.durationSeconds)
    const key = `${weapon}|${dur}`
    const g = groups.get(key)
    if (!g) groups.set(key, { weapon, durationSeconds: dur, best: e, attempts: 1 })
    else {
      g.attempts++
      if (e.totalDamage > g.best.totalDamage) g.best = e
    }
  }
  return [...groups.values()].sort((a, b) => a.weapon.localeCompare(b.weapon) || a.durationSeconds - b.durationSeconds)
}

/** Cumulative local damage over time rebuilt from hits (used for trial comparisons). */
export function cumulativeFromHits(hits: FightLogHit[], label: string, step = 1): CurvePoint[] {
  const out: CurvePoint[] = [{ t: 0, damage: 0, slot: 0, player: label }]
  let cum = 0
  let i = 0
  const end = hits.length ? Math.ceil(hits[hits.length - 1]!.t) : 0
  for (let t = step; t <= end; t += step) {
    while (i < hits.length && hits[i]!.t <= t) cum += hits[i++]!.damage
    out.push({ t, damage: cum, slot: 0, player: label })
  }
  return out
}

export function formatDuration(seconds: number): string {
  const s = Math.max(0, Math.round(seconds))
  const m = Math.floor(s / 60)
  const r = s % 60
  return m ? `${m}:${String(r).padStart(2, '0')}` : `${r}s`
}

export function formatInt(n: number): string {
  return Math.round(n).toLocaleString('en-US')
}

export function formatDate(iso: string): string {
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return iso
  return d.toLocaleString(undefined, { year: 'numeric', month: 'short', day: '2-digit', hour: '2-digit', minute: '2-digit' })
}

/** "06 Sep 00:09" for narrow columns. */
export function formatShortDate(iso: string): string {
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return iso.slice(0, 16)
  return d.toLocaleString(undefined, { month: 'short', day: '2-digit', hour: '2-digit', minute: '2-digit' })
}

export function pct(x: number, digits = 1): string {
  return `${(x * 100).toFixed(digits)}%`
}
