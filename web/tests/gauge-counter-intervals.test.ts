import { describe, expect, it } from 'vitest'
import { normalizeLog } from '../src/data/parse'
import { observedCounterIntervals } from '../src/data/gaugeCounterIntervals'

describe('observed local gauge counter changes (not exact proc or break events)', () => {
  it('reports only consecutive positive counter changes in capture intervals', () => {
    const part = (value: number | undefined) => value === undefined ? [] :
      [{ part: 2, name: 'Head', remaining: 30, max: 100, completedFlinches: value }]
    const ailment = (value: number | undefined) => value === undefined ? [] :
      [{ id: 7, name: 'KO', buildup: 30, maxBuildup: 100, procs: value }]
    const reading = (t: unknown, m: string, p: number | undefined, a: number | undefined,
      extras: Record<string, unknown> = {}) =>
      ({ t, monster: m, source: 'local-unverified', parts: part(p), ailments: ailment(a), ...extras })
    const log = normalizeLog({ questName: 'Counter intervals', players: [], gaugeSamples: [
      reading(0, 'm1', 0, 0),
      reading(0, 'm2', 7, 7),
      reading(2, 'm1', 1, 2), // two observed changes in [0, 2], not at t=2
      reading(2, 'm2', 8, 8),
      reading(4, 'm1', 1, 2), // unchanged
      reading('bad', 'm1', 2, 3, { clockBoundary: true }), // gap marker; not an event
      reading(6, 'm1', 3, 4, { clockBoundary: false }),
      reading(8, 'm1', 4, 5), // new valid interval after the gap
      reading(20, 'm1', 5, 6), // >6s gap; no event
      reading(22, 'm1', 1, 0), // counter reset; do not infer a proc
      reading(24, 'm1', 2, 1), // after reset, a new observed interval
      reading(26, 'm1', undefined, undefined), // missing counter breaks continuity
      reading(28, 'm1', 3, 2),
      reading(30, 'm1', 4, 3, { clockBoundary: true }), // boundary itself breaks
      reading(32, 'm1', 5, 4, { clockBoundary: false }),
      reading(34, 'm1', 6, 5, { source: 'another-source' }), // provenance changed
      reading(36, 'm1', 7, 6, { source: 'another-source' }),
    ] })
    expect(observedCounterIntervals(log.gaugeSamples, 'm1', 'part:2'))
      .toEqual([
        { startT: 0, endT: 2, before: 0, after: 1, increase: 1 },
        { startT: 6, endT: 8, before: 3, after: 4, increase: 1 },
        { startT: 22, endT: 24, before: 1, after: 2, increase: 1 },
        { startT: 30, endT: 32, before: 4, after: 5, increase: 1 },
        { startT: 34, endT: 36, before: 6, after: 7, increase: 1 },
      ])
    expect(observedCounterIntervals(log.gaugeSamples, 'm1', 'ailment:7')
      .map(x => [x.startT, x.endT, x.increase]))
      .toEqual([[0, 2, 2], [6, 8, 1], [22, 24, 1], [30, 32, 1], [34, 36, 1]])
    expect(observedCounterIntervals(log.gaugeSamples, 'm2', 'part:2'))
      .toEqual([{ startT: 0, endT: 2, before: 7, after: 8, increase: 1 }])
    expect(observedCounterIntervals(log.gaugeSamples, 'm1', 'stamina')).toEqual([])
  })
  it('uses the first valid proc counter among duplicate partial ailment IDs without inventing intervals', () => {
    const ailment = (extra: Record<string, unknown>) => ({ id: 7, name: 'KO', ...extra })
    const reading = (t: unknown, monster: string, ailments: Record<string, unknown>[],
      extra: Record<string, unknown> = {}) =>
      ({ t, monster, source: 'local-unverified', ailments, ...extra })
    const log = normalizeLog({ questName: 'Partial duplicated proc counters', players: [], gaugeSamples: [
      reading(0, 'm1', [ailment({ buildup: 30, maxBuildup: 100, procs: 0 })]),
      reading(0, 'm2', [ailment({ buildup: 30, maxBuildup: 100, procs: 9 })]),
      reading(2, 'm1', [
        ailment({ duration: 5, maxDuration: 10 }),
        ailment({ procs: -2, buildup: 33, maxBuildup: 100 }),
        ailment({ buildup: 50, maxBuildup: 100, procs: 1 }),
      ]),
      reading(2, 'm2', [ailment({ duration: 1, maxDuration: 10 }), ailment({ procs: 10 })]),
      reading(4, 'm1', [ailment({ duration: 4, maxDuration: 10 }), ailment({ procs: 3 })]),
      reading(6, 'm1', [ailment({ duration: 2, maxDuration: 10 })]), // unknown: reset
      reading(8, 'm1', [ailment({ procs: 4 })]),
      reading(10, 'm1', [ailment({ duration: 2, maxDuration: 10 }), ailment({ procs: 5 })]),
      reading(12, 'm1', [ailment({ procs: 6 })], { clockBoundary: true }), // boundary
      reading(14, 'm1', [ailment({ procs: 7 })], { source: 'unknown' }), // changed provenance
      reading(16, 'm1', [ailment({ procs: 8 })], { source: 'unknown' }),
      reading(18, 'm1', [ailment({ procs: -1 }), ailment({ duration: 2, maxDuration: 10 })]),
      reading(20, 'm1', [ailment({ procs: 9 })]),
      reading(22, 'm1', [ailment({ procs: 10 })]),
    ] })
    expect(observedCounterIntervals(log.gaugeSamples, 'm1', 'ailment:7')).toEqual([
      { startT: 0, endT: 2, before: 0, after: 1, increase: 1 },
      { startT: 2, endT: 4, before: 1, after: 3, increase: 2 },
      { startT: 8, endT: 10, before: 4, after: 5, increase: 1 },
      { startT: 14, endT: 16, before: 7, after: 8, increase: 1 },
      { startT: 20, endT: 22, before: 9, after: 10, increase: 1 },
    ])
    expect(observedCounterIntervals(log.gaugeSamples, 'm2', 'ailment:7')).toEqual([
      { startT: 0, endT: 2, before: 9, after: 10, increase: 1 },
    ])
    expect(observedCounterIntervals(log.gaugeSamples, 'm1', 'ailment-duration:7')).toEqual([])
    expect(observedCounterIntervals(log.gaugeSamples, 'm1', 'ailment:8')).toEqual([])
  })

})
