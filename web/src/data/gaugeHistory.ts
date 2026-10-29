import type { FightLogGaugeSample } from '../schema/fightlog'

export interface GaugeOption { key: string; label: string }
export interface GaugePoint { t: number; value: number; segment: string; source: string }

/** Read-only, sampled local observations. No proc events or attacker attribution inferred. */
export function availableGaugeMetrics(samples: FightLogGaugeSample[], monster: string): GaugeOption[] {
  const ailments = new Map<number, { name: string; buildup: boolean; duration: boolean }>()
  const parts = new Map<number, string>()
  let stamina = false
  for (const sample of samples) {
    if (sample.monster !== monster) continue
    if (fraction(sample.stamina, sample.maxStamina) !== null) stamina = true
    for (const a of sample.ailments ?? []) {
      const buildup = fraction(a.buildup, a.maxBuildup) !== null
      const duration = fraction(a.duration, a.maxDuration) !== null
      if (!buildup && !duration) continue
      const entry = ailments.get(a.id) ?? { name: a.name, buildup: false, duration: false }
      entry.name = a.name
      entry.buildup ||= buildup
      entry.duration ||= duration
      ailments.set(a.id, entry)
    }
    for (const p of sample.parts ?? []) {
      if (fraction(p.remaining, p.max) !== null) parts.set(p.part, p.name)
    }
  }
  return [
    ...(stamina ? [{ key: 'stamina', label: 'Stamina remaining' }] : []),
    ...[...ailments].sort(([a], [b]) => a - b).flatMap(([id, a]) => [
      ...(a.buildup ? [{ key: `ailment:${id}`, label: `${a.name} buildup` }] : []),
      ...(a.duration ? [{ key: `ailment-duration:${id}`, label: `${a.name} duration remaining` }] : []),
    ]),
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
  if (kind === 'ailment' || kind === 'ailment-duration') {
    const entry = (s.ailments ?? []).find(a => a.id === number)
    if (!entry) return null
    return kind === 'ailment' ? fraction(entry.buildup, entry.maxBuildup)
      : fraction(entry.duration, entry.maxDuration)
  }
  if (kind === 'part') {
    const entry = (s.parts ?? []).find(p => p.part === number)
    return entry ? fraction(entry.remaining, entry.max) : null
  }
  return null
}


/** Counts stored, *not expected*, observations for one monster and selected metric.
 * No hunt-wide coverage percentage or authority can be inferred from this log. */
export interface GaugeCoverage {
  observations: number
  usable: number
  missingMetric: number
  invalidTime: number
  outOfOrder: number
  gapsOver6s: number
  sourceChanges: number
  clockBoundaries: number
  nonDefaultSource: number
}

/** Pass no metric to count only capture quality; metric-specific fields stay zero. */
export function gaugeCoverage(samples: FightLogGaugeSample[], monster: string, metric?: string): GaugeCoverage {
  const coverage: GaugeCoverage = {
    observations: 0, usable: 0, missingMetric: 0, invalidTime: 0,
    outOfOrder: 0, gapsOver6s: 0, sourceChanges: 0, clockBoundaries: 0,
    nonDefaultSource: 0,
  }
  let previousCaptureT: number | undefined
  // Source transitions are properties of capture order, even if t is damaged
  // or goes backwards. Time-comparable pairs are only needed for gap counts.
  let previousSource: string | undefined
  let lastUsableT: number | undefined
  let wasAdjusted = false
  for (const sample of samples) {
    if (sample.monster !== monster) continue
    coverage.observations++
    const boundary = sample.clockBoundary ?? (Boolean(sample.clockAdjusted) && !wasAdjusted)
    wasAdjusted = Boolean(sample.clockAdjusted)
    if (boundary) coverage.clockBoundaries++
    if (sample.source !== 'local-unverified') coverage.nonDefaultSource++
    if (previousSource !== undefined && sample.source !== previousSource) coverage.sourceChanges++
    previousSource = sample.source
    const t = sample.t
    if (!Number.isFinite(t) || t < 0) {
      coverage.invalidTime++
      previousCaptureT = undefined
      continue
    }
    // Clock corrections and backwards timestamps are not elapsed-time evidence.
    // Do not reuse a regressing timestamp as the next gap baseline either.
    const regressed = previousCaptureT !== undefined && t < previousCaptureT
    if (!boundary && !regressed && previousCaptureT !== undefined && t - previousCaptureT > 6) {
      coverage.gapsOver6s++
    }
    previousCaptureT = regressed ? undefined : t
    if (metric === undefined) continue
    if (metricValue(sample, metric) === null) {
      coverage.missingMetric++
    } else if (lastUsableT !== undefined && t < lastUsableT) {
      coverage.outOfOrder++
    } else {
      coverage.usable++
      lastUsableT = t
    }
  }
  return coverage
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
  let lastSource: string | undefined
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
    // A different observation source cannot be interpolated into the last curve,
    // even if both samples are numerically valid and close in time.
    if (missing || clockBoundary || (lastSource !== undefined && lastSource !== sample.source)
      || (lastT !== undefined && t - lastT > 6)) segment++
    points.push({ t, value, segment: `segment-${segment}`, source: sample.source })
    lastSource = sample.source
    lastT = t
    missing = false
  }
  return points
}
