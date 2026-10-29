import { describe, expect, it } from 'vitest'
import { normalizeLog } from '../src/data/parse'
import { availableGaugeMetrics, gaugeHistoryPoints, isolatedGaugePoints } from '../src/data/gaugeHistory'
import { gaugeHistoryChart } from '../src/charts/definitions'

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

})
