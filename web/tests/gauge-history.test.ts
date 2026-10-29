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

})
