import type { FightLogGaugeSample } from '../schema/fightlog'
import { isGaugeI32, isObservedGaugeCounter } from './gaugeInteger'

export interface CounterInterval {
  startT: number
  endT: number
  before: number
  after: number
  increase: number
}

/** A counter changed between two captures, NOT an event timestamp or hunter attribution. */
export function observedCounterIntervals(
  samples: FightLogGaugeSample[], monster: string, metric: string,
): CounterInterval[] {
  if (!metric.startsWith('part:') && !metric.startsWith('ailment:')) return []
  const [kind, rawId] = metric.split(':')
  const id = Number(rawId)
  if (!isGaugeI32(id)) return []

  const result: CounterInterval[] = []
  let last: { t: number; count: number; source: string } | undefined
  let wasAdjusted = false

  for (const sample of samples) {
    if (sample.monster !== monster) continue
    const boundary = sample.clockBoundary ?? (Boolean(sample.clockAdjusted) && !wasAdjusted)
    wasAdjusted = Boolean(sample.clockAdjusted)
    // Imported records can repeat part or ailment IDs. Prefer the first
    // valid recorded nonnegative counter in a single entry with the ID;
    // an earlier unreadable duplicate must not hide a later real counter.
    // Never merge entries or treat an unknown counter as zero.
    const count = kind === 'part'
      ? sample.parts?.find(p => p.part === id && isObservedGaugeCounter(p.completedFlinches))?.completedFlinches
      : sample.ailments?.find(a => a.id === id && isObservedGaugeCounter(a.procs))?.procs
    // Never substitute an absent or invalid counter with 0. The importer may
    // preserve a partial observation whose numeric progress was unreadable.
    if (!isObservedGaugeCounter(count)
      || !Number.isFinite(sample.t) || sample.t < 0) {
      last = undefined
      continue
    }
    const current = { t: sample.t, count, source: sample.source }
    if (last && !boundary && current.source === last.source
      && current.t > last.t && current.t - last.t <= 6
      && current.count > last.count) {
      result.push({
        startT: last.t, endT: current.t, before: last.count,
        after: current.count, increase: current.count - last.count,
      })
    }
    last = current
  }
  return result
}

/** All observed positive changes across recorded counters for one monster.
 * Limited to the most recent five intervals by stored capture end time.
 * Never an exact proc/break timestamp, a completed hit, or hunter attribution.
 * Counter-only observations remain eligible even when no chart metric exists.
 */
export interface CounterTimelineEntry extends CounterInterval {
  key: string
  label: string
}

export function observedCounterTimeline(
  samples: FightLogGaugeSample[], monster: string,
): CounterTimelineEntry[] {
  const labels = new Map<string, string>()
  for (const sample of samples) {
    if (sample.monster !== monster) continue
    for (const a of sample.ailments ?? []) {
      if (isObservedGaugeCounter(a.procs)) {
        labels.set(`ailment:${a.id}`, `${a.name} sampled proc counter`)
      }
    }
    for (const p of sample.parts ?? []) {
      if (isObservedGaugeCounter(p.completedFlinches)) {
        labels.set(`part:${p.part}`, `${p.name} sampled flinch counter`)
      }
    }
  }
  const events: CounterTimelineEntry[] = []
  for (const [key, label] of labels) {
    for (const interval of observedCounterIntervals(samples, monster, key)) {
      events.push({ key, label, ...interval })
    }
  }
  events.sort((a, b) => b.endT - a.endT || b.startT - a.startT
    || (a.key < b.key ? -1 : a.key > b.key ? 1 : 0))
  return events.slice(0, 5)
}
