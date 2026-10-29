import { describe, expect, it } from 'vitest'
import { normalizeLog } from '../src/data/parse'
import { availableGaugeMetrics, gaugeHistoryPoints, isolatedGaugePoints, gaugeCoverage } from '../src/data/gaugeHistory'
import { gaugeHistoryChart } from '../src/charts/definitions'
import { observedCounterIntervals } from '../src/data/gaugeCounterIntervals'

describe('experimental gauge history', () => {
  it('preserves old logs, missing values and multi-monster identity', () => {
    const old = normalizeLog({ questName: 'Old hunt', players: [] })
    expect(old.gaugeSamples).toEqual([])
    expect(availableGaugeMetrics(old.gaugeSamples, 'm1')).toEqual([])
    expect(gaugeHistoryPoints(old.gaugeSamples, 'm1', 'stamina')).toEqual([])

    const log = normalizeLog({ questName: 'Experimental', players: [], gaugeSamples: [
      { t: 0, monster: 'm1', source: 'local-unverified', stamina: 80, maxStamina: 100, parts: [{part: 2, name: 'Head', remaining: 60, max: 100}], ailments: [{id: 7, name: 'KO', buildup: 20, maxBuildup: 100}] },
      { t: 1, monster: 'm2', source: 'local-unverified', stamina: 40, maxStamina: 100 },
      { t: 2, monster: 'm1', source: 'local-unverified', stamina: null },
      { t: 4, monster: 'm1', source: 'local-unverified', stamina: 60, maxStamina: 100 },
      { t: 14, monster: 'm1', source: 'local-unverified', stamina: 50, maxStamina: 100 },
      { t: 15, monster: 'm1', source: 'local-unverified', stamina: 40, maxStamina: 100, clockAdjusted: true },
      { t: 16, monster: 'm1', source: 'local-unverified', stamina: 30, maxStamina: 100 },
      { t: 18, monster: 'm1', source: 'local-unverified', stamina: 100, maxStamina: 0 },
      { t: 40, monster: 'm1', source: 'local-unverified', stamina: 20, maxStamina: 100 },
    ] })
    expect(availableGaugeMetrics(log.gaugeSamples, 'm1')).toEqual([
      { key: 'stamina', label: 'Stamina remaining' },
      { key: 'ailment:7', label: 'KO buildup' },
      { key: 'part:2', label: 'Head flinch remaining' },
    ])
    expect(gaugeHistoryPoints(log.gaugeSamples, 'm1', 'stamina').map(({t, value, segment}) => [t,value,segment])).toEqual([
      [0,80,'segment-1'], [4,60,'segment-2'], [14,50,'segment-3'], [15,40,'segment-4'], [16,30,'segment-4'], [40,20,'segment-5'],
    ])
    const points = gaugeHistoryPoints(log.gaugeSamples, 'm1', 'stamina')
    expect(isolatedGaugePoints(points).map(p => p.t)).toEqual([0, 4, 14, 40])
    const chart = gaugeHistoryChart(points, 'Stamina') as unknown as {scales: {y: {scale: {domain: () => number[]}}}}
    expect(chart.scales.y.scale.domain()).toEqual([0, 100])
    expect(gaugeHistoryPoints(log.gaugeSamples, 'm2', 'stamina').map(p => p.value)).toEqual([40])
    expect(gaugeHistoryPoints(log.gaugeSamples, 'm1', 'ailment:7').map(p => p.value)).toEqual([20])
    expect(gaugeHistoryPoints(log.gaugeSamples, 'm1', 'part:2').map(p => p.value)).toEqual([60])
  })

  it('uses one per-monster boundary for a reset followed by three valid records', () => {
    const times = [100, 102, 104, 106, 108, 110]
    const values = [10, 20, 30, 40, 50, 60]
    for (const legacy of [false, true]) {
      const log = normalizeLog({ questName: 'corrected clock', players: [],
        gaugeSamples: times.map((t, i) => ({
          t, monster: 'm1', source: 'local-unverified', stamina: values[i], maxStamina: 100,
          clockAdjusted: i >= 2,
          ...(legacy ? {} : { clockBoundary: i === 2 }),
        })),
      })
      const points = gaugeHistoryPoints(log.gaugeSamples, 'm1', 'stamina')
      expect(points.map(p => p.segment)).toEqual([
        'segment-1', 'segment-1', 'segment-2', 'segment-2', 'segment-2', 'segment-2',
      ])
      expect(isolatedGaugePoints(points)).toEqual([])
    }
  })


  it('treats malformed imported optional gauge entries as unavailable, not chart data', () => {
    const log = normalizeLog({
      questName: 'Imported mixed data', players: [],
      monsters: [{ id: 'm1', name: 'Anjanath', type: 1, variant: 0, maxHealth: 1000, lastHealth: 500, firstSeenT: 0 }],
      gaugeSamples: [
        null, false, 42, [], {}, { t: 'invalid', monster: 'm1' },
        { t: 0, monster: 'm1', source: 'local-unverified', stamina: 50, maxStamina: 100,
          ailments: [null, 17, {}, { id: 7, name: 'KO', buildup: 20, maxBuildup: 100 }, { id: 'bad', name: 'Invalid' }],
          parts: [null, 'nope', { part: 2, name: 'Head', remaining: 80, max: 100 }] },
        { t: 2, monster: 'm1', source: 'local-unverified', stamina: 40, maxStamina: 100,
          ailments: [null, { id: 7, name: 'KO', buildup: 30, maxBuildup: 100 }],
          parts: { invalid: true } },
        { t: 4, monster: 'm1', source: 'local-unverified', stamina: 'not numeric', maxStamina: 100,
          ailments: [null], parts: [false] },
      ],
    })

    expect(log.gaugeSamples).toHaveLength(4)
    expect(log.gaugeSamples[0]?.t).toBe(-1) // timestamp is invalid: non-plottable gap
    expect(log.gaugeSamples[0]?.stamina).toBeNull()
    expect(log.gaugeSamples[1]?.ailments).toHaveLength(1)
    expect(log.gaugeSamples[1]?.parts).toHaveLength(1)
    expect(log.gaugeSamples[2]?.parts).toEqual([])
    expect(log.gaugeSamples[3]?.stamina).toBeNull()
    expect(availableGaugeMetrics(log.gaugeSamples, 'm1').map(m => m.key))
      .toEqual(['stamina', 'ailment:7', 'part:2'])
    expect(gaugeHistoryPoints(log.gaugeSamples, 'm1', 'stamina').map(p => p.value))
      .toEqual([50, 40])
    expect(gaugeHistoryPoints(log.gaugeSamples, 'm1', 'ailment:7').map(p => p.value))
      .toEqual([20, 30])
    expect(gaugeHistoryPoints(log.gaugeSamples, 'm1', 'part:2').map(p => p.value))
      .toEqual([80])
  })


  it('preserves an invalid-t clock boundary without joining unrelated monster curves', () => {
    const log = normalizeLog({ questName: 'Damaged capture', players: [], gaugeSamples: [
      { t: 0, monster: 'm1', source: 'local-unverified', stamina: 80, maxStamina: 100 },
      { t: 0, monster: 'm2', source: 'local-unverified', stamina: 80, maxStamina: 100 },
      { t: 'invalid', monster: 'm1', clockBoundary: true, clockAdjusted: true,
        stamina: 50, maxStamina: 100 },
      { t: 4, monster: 'm1', source: 'local-unverified', clockBoundary: false, clockAdjusted: true,
        stamina: 40, maxStamina: 100 },
      { t: 4, monster: 'm2', source: 'local-unverified', stamina: 40, maxStamina: 100 },
    ] })
    const damaged = log.gaugeSamples.find(s => s.monster === 'm1' && s.t < 0)
    expect(damaged).toMatchObject({ t: -1, clockBoundary: true, stamina: null, maxStamina: null,
      parts: [], ailments: [] })
    const m1 = gaugeHistoryPoints(log.gaugeSamples, 'm1', 'stamina')
    expect(m1.map(p => [p.t, p.segment])).toEqual([[0, 'segment-1'], [4, 'segment-2']])
    expect(isolatedGaugePoints(m1)).toHaveLength(2)
    const m2 = gaugeHistoryPoints(log.gaugeSamples, 'm2', 'stamina')
    expect(m2.map(p => [p.t, p.segment])).toEqual([[0, 'segment-1'], [4, 'segment-1']])
  })

  it('does not interpolate sampled percentages across observation-source changes', () => {
    const observation = (t: number, monster: string, source: string, stamina: number) =>
      ({ t, monster, source, stamina, maxStamina: 100 })
    const log = normalizeLog({ questName: 'Mixed-source import', players: [], gaugeSamples: [
      observation(0, 'm1', 'local-unverified', 10),
      observation(0, 'm2', 'local-unverified', 30),
      observation(2, 'm1', 'local-unverified', 20),
      observation(2, 'm2', 'local-unverified', 40),
      observation(4, 'm1', 'unknown', 30),
      observation(6, 'm1', 'unknown', 40),
      observation(8, 'm1', 'local-unverified', 50),
      observation(10, 'm1', 'local-unverified', 60),
    ] })
    const points = gaugeHistoryPoints(log.gaugeSamples, 'm1', 'stamina')
    expect(points.map(p => [p.t, p.segment, p.source])).toEqual([
      [0, 'segment-1', 'local-unverified'], [2, 'segment-1', 'local-unverified'],
      [4, 'segment-2', 'unknown'], [6, 'segment-2', 'unknown'],
      [8, 'segment-3', 'local-unverified'], [10, 'segment-3', 'local-unverified'],
    ])
    expect(isolatedGaugePoints(points)).toEqual([])
    expect(gaugeHistoryPoints(log.gaugeSamples, 'm2', 'stamina').map(p => p.segment))
      .toEqual(['segment-1', 'segment-1'])
  })

  it('reports metric-specific stored-sample quality without inventing hunt coverage', () => {
    const log = normalizeLog({ questName: 'Partial capture', players: [],
      gaugeSamplesDropped: 9, gaugeClockDiscontinuities: 1,
      gaugeSamples: [
        { t: 0, monster: 'm1', source: 'local-unverified', stamina: 10, maxStamina: 100 },
        { t: 0, monster: 'm2', source: 'local-unverified', stamina: 40, maxStamina: 100 },
        { t: 2, monster: 'm1', source: 'local-unverified', stamina: 20, maxStamina: 100 },
        { t: 2, monster: 'm2', source: 'local-unverified', stamina: 50, maxStamina: 100 },
        { t: 4, monster: 'm1', source: 'local-unverified', stamina: null },
        { t: 12, monster: 'm1', source: 'unknown', stamina: 30, maxStamina: 100 },
        { t: 14, monster: 'm1', source: 'unknown', stamina: 40, maxStamina: 100 },
        { t: 'bad', monster: 'm1', source: 'local-unverified', clockBoundary: true, stamina: 90, maxStamina: 100 },
        { t: 16, monster: 'm1', source: 'local-unverified', stamina: 50, maxStamina: 100 },
        { t: 18, monster: 'm1', source: 'local-unverified', stamina: 60, maxStamina: 100 },
        { t: 17, monster: 'm1', source: 'local-unverified', stamina: 65, maxStamina: 100 },
        { t: 20, monster: 'm1', source: 'local-unverified', stamina: null },
        { t: 22, monster: 'm1', source: 'local-unverified', stamina: 70, maxStamina: 100 },
      ] })
    expect(gaugeCoverage(log.gaugeSamples, 'm1', 'stamina')).toEqual({
      observations: 11, usable: 7, missingMetric: 2, invalidTime: 1,
      outOfOrder: 1, gapsOver6s: 1, sourceChanges: 2, clockBoundaries: 1,
      nonDefaultSource: 2,
    })
    expect(gaugeCoverage(log.gaugeSamples, 'm2', 'stamina')).toEqual({
      observations: 2, usable: 2, missingMetric: 0, invalidTime: 0,
      outOfOrder: 0, gapsOver6s: 0, sourceChanges: 0, clockBoundaries: 0,
      nonDefaultSource: 0,
    })
    expect(gaugeCoverage([], 'm1', 'stamina').observations).toBe(0)
    expect(log.gaugeSamplesDropped).toBe(9) // hunt-wide; never allocated to m1 or m2
  })

  it('keeps alternating one-point source sequences isolated', () => {
    const log = normalizeLog({ questName: 'Alternating sources', players: [], gaugeSamples: [
      { t: 0, monster: 'm1', source: 'local-unverified', stamina: 10, maxStamina: 100 },
      { t: 2, monster: 'm1', source: 'unknown', stamina: 20, maxStamina: 100 },
      { t: 4, monster: 'm1', source: 'local-unverified', stamina: 30, maxStamina: 100 },
    ] })
    const points = gaugeHistoryPoints(log.gaugeSamples, 'm1', 'stamina')
    expect(points.map(p => p.segment)).toEqual(['segment-1', 'segment-2', 'segment-3'])
    expect(isolatedGaugePoints(points)).toHaveLength(3)
    expect(gaugeCoverage(log.gaugeSamples, 'm1', 'stamina').sourceChanges).toBe(2)
  })

  it('counts source switches across invalid and backward timestamps in capture order', () => {
    const log = normalizeLog({ questName: 'Mixed timestamps', players: [], gaugeSamples: [
      { t: 0, monster: 'm1', source: 'local-unverified', stamina: 10, maxStamina: 100 },
      { t: 0, monster: 'm2', source: 'local-unverified', stamina: 10, maxStamina: 100 },
      { t: 'bad', monster: 'm1', source: 'unknown', stamina: 20, maxStamina: 100 },
      { t: 2, monster: 'm1', source: 'unknown', stamina: 30, maxStamina: 100 },
      { t: 1, monster: 'm1', source: 'local-unverified', stamina: 40, maxStamina: 100 },
      { t: 3, monster: 'm1', source: 'local-unverified', stamina: 50, maxStamina: 100 },
      { t: 4, monster: 'm2', source: 'local-unverified', stamina: 20, maxStamina: 100 },
    ] })
    const m1 = gaugeCoverage(log.gaugeSamples, 'm1', 'stamina')
    expect(m1).toMatchObject({
      observations: 5, invalidTime: 1, outOfOrder: 1,
      sourceChanges: 2, gapsOver6s: 0, nonDefaultSource: 2,
    })
    expect(gaugeCoverage(log.gaugeSamples, 'm2', 'stamina').sourceChanges).toBe(0)
  })

  it('does not infer capture gaps from backwards timestamps or clock correction boundaries', () => {
    const reading = (t: number, monster: string, options: Record<string, unknown> = {}) =>
      ({ t, monster, source: 'local-unverified', stamina: 50, maxStamina: 100, ...options })
    const cases = [
      {
        name: 'backwards time resets gap comparison but later comparable gaps still count',
        samples: [reading(10, 'm1'), reading(2, 'm1'), reading(11, 'm1'), reading(19, 'm1')],
        gaps: 1, boundaries: 0,
      },
      {
        name: 'explicit correction boundary resets the elapsed interval',
        samples: [reading(0, 'm1'), reading(20, 'm1', { clockBoundary: true }),
          reading(22, 'm1', { clockBoundary: false }), reading(30, 'm1')],
        gaps: 1, boundaries: 1,
      },
      {
        name: 'legacy clockAdjusted transition breaks comparison once, not every adjusted point',
        samples: [reading(0, 'm1'), reading(20, 'm1', { clockAdjusted: true }),
          reading(22, 'm1', { clockAdjusted: true }), reading(30, 'm1', { clockAdjusted: true })],
        gaps: 1, boundaries: 1,
      },
    ]
    for (const { name, samples, gaps, boundaries } of cases) {
      const log = normalizeLog({ questName: name, players: [], gaugeSamples: [
        ...samples, reading(1, 'm2'), reading(20, 'm2', { clockBoundary: true }),
      ] })
      const coverage = gaugeCoverage(log.gaugeSamples, 'm1', 'stamina')
      expect({ name, gaps: coverage.gapsOver6s, boundaries: coverage.clockBoundaries })
        .toEqual({ name, gaps, boundaries })
      expect(coverage.sourceChanges).toBe(0)
      expect(gaugeCoverage(log.gaugeSamples, 'm2', 'stamina').gapsOver6s).toBe(0)
    }

    const log = normalizeLog({ questName: 'Provenance and time are independent', players: [], gaugeSamples: [
      reading(10, 'm1', { source: 'local-unverified' }),
      reading(2, 'm1', { source: 'unknown' }),
      reading(11, 'm1', { source: 'local-unverified' }),
    ] })
    expect(gaugeCoverage(log.gaugeSamples, 'm1', 'stamina')).toMatchObject({
      gapsOver6s: 0, sourceChanges: 2, outOfOrder: 1,
    })
  })

  it('preserves monster quality when every imported gauge is unplottable', () => {
    const log = normalizeLog({ questName: 'Only gauge gap markers', players: [],
      monsters: [{ id: 'm1', name: 'Anjanath', type: 1, variant: 0, maxHealth: 1000,
        lastHealth: 500, firstSeenT: 0 }],
      gaugeSamples: [
        { t: 'bad', monster: 'm1', source: 'unknown', clockBoundary: true },
        { t: 2, monster: 'm2', source: 'local-unverified', stamina: 40, maxStamina: 100 },
        { t: 'also bad', monster: 'm1', source: 'local-unverified' },
      ],
    })
    expect(availableGaugeMetrics(log.gaugeSamples, 'm1')).toEqual([])
    expect(gaugeCoverage(log.gaugeSamples, 'm1')).toEqual({
      observations: 2, usable: 0, missingMetric: 0, invalidTime: 2,
      outOfOrder: 0, gapsOver6s: 0, sourceChanges: 1,
      clockBoundaries: 1, nonDefaultSource: 1,
    })
    expect(gaugeCoverage(log.gaugeSamples, 'm2')).toMatchObject({
      observations: 1, invalidTime: 0, sourceChanges: 0, clockBoundaries: 0,
    })
    expect(gaugeCoverage(log.gaugeSamples, 'm1', 'stamina')).toMatchObject({
      observations: 2, usable: 0, missingMetric: 0, invalidTime: 2,
    })
  })

  it('plots sampled ailment duration independently of buildup and never invents missing time', () => {
    const status = (extras: Record<string, unknown>) =>
      ({ id: 7, name: 'KO', ...extras })
    const log = normalizeLog({ questName: 'Duration vs buildup', players: [], gaugeSamples: [
      { t: 0, monster: 'm1', source: 'local-unverified', ailments: [
        status({ buildup: 30, maxBuildup: 100, duration: 5, maxDuration: 10, procs: 0 })] },
      { t: 1, monster: 'm2', source: 'local-unverified', ailments: [
        status({ buildup: 40, maxBuildup: 100, duration: 4, maxDuration: 10 })] },
      { t: 2, monster: 'm1', source: 'local-unverified', ailments: [
        status({ duration: 2, maxDuration: 10 })] }, // duration-only still valid
      { t: 4, monster: 'm1', source: 'local-unverified', ailments: [
        status({ buildup: 40, maxBuildup: 100, maxDuration: 10 })] }, // missing duration != 0
      { t: 6, monster: 'm1', source: 'local-unverified', ailments: [
        status({ duration: 4, maxDuration: 10 })] },
      { t: 8, monster: 'm1', source: 'local-unverified', clockBoundary: true, ailments: [
        status({ duration: 1, maxDuration: 10 })] },
      { t: 10, monster: 'm1', source: 'local-unverified', clockBoundary: false, ailments: [
        status({ duration: 0, maxDuration: 10 })] },
      { t: 12, monster: 'm1', source: 'unknown', ailments: [
        status({ duration: 0, maxDuration: 10 })] },
      { t: 14, monster: 'm1', source: 'unknown', ailments: [
        status({ duration: 9, maxDuration: 0 })] },
      { t: 16, monster: 'm1', source: 'unknown', ailments: [
        status({ duration: 'bad', maxDuration: 10 })] },
    ] })
    expect(availableGaugeMetrics(log.gaugeSamples, 'm1')).toEqual([
      { key: 'ailment:7', label: 'KO buildup' },
      { key: 'ailment-duration:7', label: 'KO duration remaining' },
    ])
    expect(availableGaugeMetrics(log.gaugeSamples, 'm2')).toEqual([
      { key: 'ailment:7', label: 'KO buildup' },
      { key: 'ailment-duration:7', label: 'KO duration remaining' },
    ])
    expect(log.gaugeSamples.find(s => s.t === 2)?.ailments[0]).toMatchObject({
      buildup: null, maxBuildup: null, duration: 2, maxDuration: 10,
    })
    expect(log.gaugeSamples.find(s => s.t === 4)?.ailments[0]?.duration).toBeNull()
    expect(log.gaugeSamples.find(s => s.t === 16)?.ailments[0]?.duration).toBeNull()
    expect(gaugeHistoryPoints(log.gaugeSamples, 'm1', 'ailment-duration:7')
      .map(p => [p.t, p.value, p.segment])).toEqual([
      [0, 50, 'segment-1'], [2, 20, 'segment-1'],
      [6, 40, 'segment-2'], [8, 10, 'segment-3'], [10, 0, 'segment-3'],
      [12, 0, 'segment-4'],
    ])
    expect(gaugeHistoryPoints(log.gaugeSamples, 'm1', 'ailment:7').map(p => p.t))
      .toEqual([0, 4])
    expect(gaugeHistoryPoints(log.gaugeSamples, 'm2', 'ailment-duration:7').map(p => p.value))
      .toEqual([40])
    expect(gaugeCoverage(log.gaugeSamples, 'm1', 'ailment-duration:7')).toMatchObject({
      observations: 9, usable: 6, missingMetric: 3, clockBoundaries: 1, sourceChanges: 1,
    })
    expect(observedCounterIntervals(log.gaugeSamples, 'm1', 'ailment-duration:7')).toEqual([])
  })

  it('exposes duration-only ailments but not absent or invalid duration as a metric', () => {
    const log = normalizeLog({ questName: 'Status duration import', players: [], gaugeSamples: [
      { t: 0, monster: 'm1', source: 'local-unverified', ailments: [
        { id: 6, name: 'Exhaust', duration: 0, maxDuration: 20 },
        { id: 7, name: 'KO', buildup: 20, maxBuildup: 100 },
        { id: 8, name: 'Unknown', duration: null, maxDuration: 30 },
      ] },
    ] })
    expect(availableGaugeMetrics(log.gaugeSamples, 'm1')).toEqual([
      { key: 'ailment-duration:6', label: 'Exhaust duration remaining' },
      { key: 'ailment:7', label: 'KO buildup' },
    ])
    expect(gaugeHistoryPoints(log.gaugeSamples, 'm1', 'ailment-duration:6')[0]?.value).toBe(0)
    expect(gaugeHistoryPoints(log.gaugeSamples, 'm1', 'ailment-duration:8')).toEqual([])
  })

})
