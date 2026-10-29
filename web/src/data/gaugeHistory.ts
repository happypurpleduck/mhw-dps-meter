import type { FightLogGaugeSample } from '../schema/fightlog'

export interface GaugeOption { key: string; label: string }
export interface GaugePoint { t: number; value: number; segment: string; source: string }

/** Read-only, sampled local observations. No proc events or attacker attribution inferred. */
export function availableGaugeMetrics(samples: FightLogGaugeSample[], monster: string): GaugeOption[] {
  const ailments = new Map<number, string>()
  const parts = new Map<number, string>()
  let stamina = false
  for (const sample of samples) {
    if (sample.monster !== monster) continue
    if (fraction(sample.stamina, sample.maxStamina) !== null) stamina = true
    for (const a of sample.ailments ?? []) {
      if (fraction(a.buildup, a.maxBuildup) !== null) ailments.set(a.id, a.name)
    }
    for (const p of sample.parts ?? []) {
      if (fraction(p.remaining, p.max) !== null) parts.set(p.part, p.name)
    }
  }
  return [
    ...(stamina ? [{ key: 'stamina', label: 'Stamina remaining' }] : []),
    ...[...ailments].sort(([a], [b]) => a - b).map(([id, name]) => ({ key: `ailment:${id}`, label: `${name} buildup` })),
    ...[...parts].sort(([a], [b]) => a - b).map(([id, name]) => ({ key: `part:${id}`, label: `${name} flinch remaining` })),
  ]
}

function fraction(value: number | null | undefined, max: number | null | undefined): number | null {
  if (typeof value !== 'number' || typeof max !== 'number' || !Number.isFinite(value)
    || !Number.isFinite(max) || max <= 0 || value < 0 || value > max * 1.01) return null
  return Math.max(0, Math.min(100, (value / max) * 100))
}

function metricValue(s: FightLogGaugeSample, key: string): number | null {
  if (key === 'stamina') return fraction(s.stamina, s.maxStamina)
  const [kind, id] = key.split(':')
  const number = Number(id)
  if (!Number.isInteger(number)) return null
  if (kind === 'ailment') {
    const entry = (s.ailments ?? []).find(a => a.id === number)
    return entry ? fraction(entry.buildup, entry.maxBuildup) : null
  }
  if (kind === 'part') {
    const entry = (s.parts ?? []).find(p => p.part === number)
    return entry ? fraction(entry.remaining, entry.max) : null
  }
  return null
}

/** Do not connect lines across missing state, a clock correction, or a >6s capture gap. */
/** Observations without a consecutive valid neighbor are not line segments. */
export function isolatedGaugePoints(points: GaugePoint[]): GaugePoint[] {
  return points.filter((p, i) => points[i - 1]?.segment !== p.segment && points[i + 1]?.segment !== p.segment)
}

export function gaugeHistoryPoints(samples: FightLogGaugeSample[], monster: string, key: string): GaugePoint[] {
  const points: GaugePoint[] = []
  let segment = 0
  let lastT: number | undefined
  let missing = true
  let wasAdjusted = false
  for (const sample of samples) {
    if (sample.monster !== monster) continue
    // clockAdjusted records a persistent offset, not a new boundary every time.
    // Older schema-3 logs omit clockBoundary: infer only the first false→true edge.
    const clockBoundary = sample.clockBoundary ?? (Boolean(sample.clockAdjusted) && !wasAdjusted)
    wasAdjusted = Boolean(sample.clockAdjusted)
    const value = metricValue(sample, key)
    const t = sample.t
    if (value === null || !Number.isFinite(t) || t < 0 || (lastT !== undefined && t < lastT)) {
      missing = true
      continue
    }
    if (missing || clockBoundary || (lastT !== undefined && t - lastT > 6)) segment++
    points.push({ t, value, segment: `segment-${segment}`, source: sample.source })
    lastT = t
    missing = false
  }
  return points
}
