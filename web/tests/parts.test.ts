import { describe, expect, it } from 'vitest'
import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { parseLog } from '../src/data/parse'
import { hitTimeline, partBreakdown, partHits } from '../src/data/analysis'

const fixture = () => parseLog(readFileSync(join(__dirname, '../../tests/fixtures/part-detail.json'), 'utf8'))

describe('part details', () => {
  it('keeps monsters, parts and hunters separate, including part zero', () => {
    const log = fixture()
    const head = partBreakdown(log, 'm1').find((r) => r.part === 0)!
    expect(head).toMatchObject({ damage: 180, hits: 5, dps: 20, avg: 36, max: 80, critRate: .4, tenderizedRate: .4 })
    expect(head.hunters.map((h) => [h.slot, h.damage, h.hits])).toEqual([[0, 160, 4], [1, 20, 1]])
    expect(head.hunters[0]!.share).toBeCloseTo(160 / 180)
    expect(head.hunters[0]!.dps).toBeCloseTo(160 / 9)
    expect(partBreakdown(log, 'm2')[0]!.damage).toBe(999)
    expect(partBreakdown(log, 'm3')).toEqual([])
  })

  it('never attributes estimated rows to a known part or displays invented crit rates', () => {
    const log = fixture()
    const unknown = partBreakdown(log, 'm1').find((r) => r.part == null)!
    expect(unknown).toMatchObject({ damage: 315, estimated: true, critRate: null, tenderizedRate: null })
    expect(partHits(log, 'm1', 0).some((h) => h.estimated)).toBe(false)
    expect(unknown.hunters.find((h) => h.slot === 0)!.critRate).toBe(0)
  })

  it('keeps teammate damage with no monster visible and never double-counts it in monster parts', () => {
    const log = fixture()
    const unassigned = partBreakdown(log, null)
    expect(unassigned).toHaveLength(1)
    expect(unassigned[0]).toMatchObject({ part: null, name: 'Unknown part', damage: 200, hits: 2, estimated: true, critRate: null })
    expect(unassigned[0]!.hunters.map((h) => [h.slot, h.damage])).toEqual([[2, 130], [1, 70]])
    const curves = hitTimeline(log, partHits(log, null, null))
    expect(curves.damage.filter((p) => p.slot === 1).at(-1)!.damage).toBe(70)
    expect(curves.damage.filter((p) => p.slot === 2).at(-1)!.damage).toBe(130)
    const allScopes = [null, ...log.monsters.map((m) => m.id)].flatMap((id) => partBreakdown(log, id))
    expect(allScopes.reduce((s, r) => s + r.damage, 0)).toBe(log.hits.reduce((s, h) => s + h.damage, 0))
    expect(partHits(log, null, 0)).toEqual([])

    log.hits = partHits(log, null, null)
    log.monsters = []
    expect(partBreakdown(log, null)[0]!.damage).toBe(200)
    // A part id without a monster cannot identify a body part, even in an exact row.
    log.hits[0] = { ...log.hits[0]!, estimated: false, part: 0, partName: 'Head' }
    expect(partBreakdown(log, null).map((r) => r.part)).toEqual([null])
  })

  it('rebuilds all curves from unsorted hits, includes time zero and the final partial bin', () => {
    const log = fixture()
    const hits = partHits(log, 'm1', 0)
    const originalOrder = hits.map((h) => h.t)
    const curves = hitTimeline(log, hits, 4)
    expect(curves.damage.filter((p) => p.slot === 0).map((p) => [p.t, p.damage]))
      .toEqual([[0, 0], [2, 40], [4, 40], [6, 120], [8, 120], [9, 160]])
    expect(curves.dps.filter((p) => p.slot === 0).map((p) => p.dps)).toEqual([0, 20, 0, 40, 0, 40])
    expect(curves.rolling.filter((p) => p.slot === 0).map((p) => p.dps)).toEqual([0, 20, 10, 20, 20, 10])
    expect(curves.damage.filter((p) => p.slot === 1).at(-1)!.damage).toBe(20)
    expect(hits.map((h) => h.t)).toEqual(originalOrder)
  })

  it('extends idle curves to hunt end and supports old logs and zero duration', () => {
    const log = fixture()
    const curves = hitTimeline(log, partHits(log, 'm1', 1), 4)
    expect(curves.damage.at(-1)!.damage).toBe(100)
    expect(curves.rolling.at(-1)!.dps).toBe(0)
    log.hits = log.hits.map((h) => ({ ...h, part: null, partName: null }))
    expect(partBreakdown(log, 'm1').map((r) => r.part)).toEqual([null])
    expect(hitTimeline(log, []).damage).toEqual([])
    log.durationSeconds = 0
    log.hits = [{ ...log.hits[0]!, t: 0, damage: 10 }]
    const zero = hitTimeline(log, log.hits)
    expect(zero.damage[0]!.damage).toBe(10)
    expect(zero.dps[0]!.dps).toBe(0)
    expect(partBreakdown(log, 'm1')[0]!.dps).toBe(0)
  })
})
