import type { FightLogGaugeSample } from '../schema/fightlog'

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
  if (!Number.isInteger(id)) return []

  const result: CounterInterval[] = []
  let last: { t: number; count: number; source: string } | undefined
  let wasAdjusted = false

  for (const sample of samples) {
    if (sample.monster !== monster) continue
    const boundary = sample.clockBoundary ?? (Boolean(sample.clockAdjusted) && !wasAdjusted)
    wasAdjusted = Boolean(sample.clockAdjusted)
    // Imported/partial records can repeat an ailment ID. Select a real
    // nonnegative proc counter, not an earlier duration-only same-ID entry.
    // Never combine values from distinct records or invent absent counters.
    const count = kind === 'part'
      ? sample.parts?.find(p => p.part === id)?.completedFlinches
      : sample.ailments?.find(a => a.id === id && typeof a.procs === 'number'
        && Number.isInteger(a.procs) && a.procs >= 0)?.procs
    // Never substitute an absent or invalid counter with 0. The importer may
    // preserve a partial observation whose numeric progress was unreadable.
    if (typeof count !== 'number' || !Number.isInteger(count) || count < 0
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
