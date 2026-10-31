import { describe, expect, it } from 'vitest'
import { readFileSync, readdirSync } from 'node:fs'
import { join } from 'node:path'
import { parseIndex, parseLog, indexEntryFromLog, hasObservedLocalDamage } from '../src/data/parse'
import {
  activeSlots,
  intervalDps,
  cumulativeFromHits,
  damageCurves,
  hitStats,
  monsterBreakdown,
  moveBreakdown,
  partBreakdown,
  hasPartData,
  rollingDps,
  trialBests,
} from '../src/data/analysis'
import { moveDisplayName, prettifyAction } from '../src/moves/names'

const SAMPLE = join(__dirname, '..', 'public', 'sample')
const files = readdirSync(SAMPLE).filter((f) => f.endsWith('.json') && f !== 'index.json')
const logs = files.map((f) => [f, parseLog(readFileSync(join(SAMPLE, f), 'utf8'))] as const)
const v2 = logs.filter(([, l]) => l.schemaVersion === 2 && l.kind === 'quest')

describe('parse', () => {
  it('reads shared synthetic Guiding Lands hit and gauge-only fixtures', () => {
    const fixtureDir = join(__dirname, '..', '..', 'tests', 'fixtures')
    const solo = parseLog(readFileSync(join(fixtureDir, 'guiding-lands-solo.json'), 'utf8'))
    const multiplayer = parseLog(readFileSync(join(fixtureDir, 'guiding-lands-multiplayer-gauge-only.json'), 'utf8'))
    expect(solo.damageScope).toBe('local-only')
    expect(solo.damageSamplesCompactions).toBe(0)
    const long = parseLog(JSON.stringify({
      questName: 'Long Guiding Lands', players: [],
      damageSamplesCompactions: 3, samples: [{ t: 0, damage: [0, 0, 0, 0] }],
    }))
    expect(long.damageSamplesCompactions).toBe(3)
    const corrupt = parseLog(JSON.stringify({
      questName: 'Imported', players: [], damageSamplesCompactions: -2,
    }))
    expect(corrupt.damageSamplesCompactions).toBe(0)
    expect(solo.questId).toBe(0)
    expect(solo.stageId).toBe(109)
    expect(hasObservedLocalDamage(solo)).toBe(true)
    expect(solo.players).toHaveLength(1)
    expect(solo.players[0]!.damage).toBe(360)
    expect(solo.players[0]!.percent).toBe(100) // internal only, not a party-share UI claim
    expect(solo.hits).toHaveLength(2)
    expect(solo.samples).toHaveLength(61) // t=0,2,...120 from the two-second writer
    expect(solo.samples[0]).toEqual({ t: 0, damage: [0, 0, 0, 0] })
    expect(solo.samples.find(s => s.t === 8)!.damage[0]).toBe(0)
    expect(solo.samples.find(s => s.t === 10)!.damage[0]).toBe(120)
    expect(solo.samples.find(s => s.t === 118)!.damage[0]).toBe(120)
    expect(solo.samples.find(s => s.t === 120)!.damage[0]).toBe(360)
    expect(solo.samples.every(s =>
      s.damage[0]! <= solo.hits.filter(h => h.t <= s.t).reduce((sum, h) => sum + h.damage, 0)
    )).toBe(true)
    expect(solo.gaugeSamples.every((s) => s.source === 'local-unverified')).toBe(true)
    expect(indexEntryFromLog(solo, 'guiding-lands-solo.json').damageScope).toBe('local-only')
    expect(multiplayer.damageScope).toBe('local-only')
    expect(multiplayer.questId).toBe(0)
    expect(hasObservedLocalDamage(multiplayer)).toBe(false)
    expect(multiplayer.players).toHaveLength(0)
    expect(multiplayer.hits).toHaveLength(0)
    expect(multiplayer.samples).toEqual([{ t: 0, damage: [0, 0, 0, 0] }])
    expect(multiplayer.monsters.map((m) => m.id)).toEqual(['m1', 'm2'])
    expect(multiplayer.gaugeSamples.map((s) => s.monster)).toEqual(['m1', 'm2'])
    expect(multiplayer.gaugeSamples.every((s) => s.source === 'local-unverified')).toBe(true)
    expect(indexEntryFromLog(multiplayer, 'guiding-lands-multiplayer-gauge-only.json').totalDamage).toBe(0)
  })
  it('retains local-only Guiding Lands damage coverage without changing old quests', () => {
    const guidingLands = parseLog(JSON.stringify({
      schemaVersion: 3, kind: 'quest', questName: 'The Guiding Lands',
      questId: 0, stageId: 109, damageScope: 'local-only', players: [],
      gaugeSamples: [{ t: 2, monster: 'm1', source: 'local-unverified' }],
    }))
    expect(guidingLands.damageScope).toBe('local-only')
    expect(guidingLands.questId).toBe(0)
    expect(guidingLands.gaugeSamples).toHaveLength(1)
    expect(indexEntryFromLog(guidingLands, 'guiding-lands.json').damageScope).toBe('local-only')
    const parsedIndex = parseIndex(JSON.stringify([{
      file: 'guiding-lands.json', questName: 'The Guiding Lands',
      damageScope: 'local-only', questId: 0, totalDamage: 0,
    }]))
    expect(parsedIndex[0]!.damageScope).toBe('local-only')
    const regular = parseLog(JSON.stringify({ questName: 'Normal hunt', players: [] }))
    expect(regular.damageScope).toBeUndefined()
    expect(hasObservedLocalDamage(regular)).toBe(true)
    expect(indexEntryFromLog(regular, 'normal.json').damageScope).toBeUndefined()
    const unknownScope = parseLog(JSON.stringify({ questName: 'Future hunt', players: [], damageScope: 'host-verified' }))
    expect(unknownScope.damageScope).toBe('host-verified')
    expect(hasObservedLocalDamage(unknownScope)).toBe(false)
    const futureIndex = parseIndex(JSON.stringify([{
      file: 'future.json', questName: 'Future', players: ['Local'],
      totalDamage: 9999, damageScope: 'host-verified',
    }]))
    expect(futureIndex[0]!.damageScope).toBe('host-verified')
  })

  it('reads every sample file, schema 1 included', () => {
    expect(logs.length).toBeGreaterThan(3)
    for (const [, log] of logs) {
      expect(log.players.length).toBeGreaterThan(0)
      expect(Array.isArray(log.hits)).toBe(true)
    }
    const v1 = logs.find(([, l]) => l.schemaVersion === 1)
    expect(v1).toBeDefined()
    expect(v1![1].hits).toEqual([])
    expect(v1![1].players[0]!.isLocal).toBe(true)
  })

  it('index.json matches what indexEntryFromLog derives', () => {
    const index = parseIndex(readFileSync(join(SAMPLE, 'index.json'), 'utf8'))
    for (const [file, log] of logs) {
      const entry = index.find((e) => e.file === file)
      expect(entry, file).toBeDefined()
      const derived = indexEntryFromLog(log, file)
      expect(entry!.totalDamage).toBe(derived.totalDamage)
      expect(entry!.kind).toBe(derived.kind)
    }
  })
})

describe('analysis', () => {
  it('hit damage sums close to the award total for the local hunter', () => {
    for (const [file, log] of v2) {
      const me = log.players.find((p) => p.isLocal)!
      const stats = hitStats(log.hits)
      // Hooked hits and the quest-award table agree to within ~3% in real logs
      // (the award table lags the hook by a poll or two at quest end).
      expect(Math.abs(stats.damage - me.damage) / me.damage, file).toBeLessThan(0.03)
      expect(stats.critRate).toBeGreaterThan(0)
      expect(stats.critRate).toBeLessThan(1)
    }
  })

  it('groups moves by action name and shares sum to 1', () => {
    const [, log] = v2[0]!
    const rows = moveBreakdown(log.hits, (k) => moveDisplayName('DualBlades', k))
    expect(rows.length).toBeGreaterThan(5)
    expect(rows[0]!.damage).toBeGreaterThanOrEqual(rows[1]!.damage)
    expect(rows.reduce((s, r) => s + r.share, 0)).toBeCloseTo(1, 6)
    expect(rows.reduce((s, r) => s + r.hits, 0)).toBe(log.hits.length)
    const ranbu = rows.find((r) => r.key === 'WP_02::RANBU')
    expect(ranbu?.name).toBe('Blade Dance')
  })

  it('builds one curve per active slot with monotonic cumulative damage', () => {
    const [, log] = v2.find(([, l]) => l.players.length > 1)!
    const slots = activeSlots(log)
    expect(slots.length).toBe(log.players.length)
    const curves = damageCurves(log)
    for (const slot of slots) {
      const series = curves.filter((p) => p.slot === slot)
      expect(series.length).toBe(log.samples.length)
      for (let i = 1; i < series.length; i++) expect(series[i]!.damage).toBeGreaterThanOrEqual(series[i - 1]!.damage)
    }
  })

  it('rolling DPS is never negative and has a point per sample', () => {
    const [, log] = v2[0]!
    const rate = rollingDps(log, 20)
    expect(rate.length).toBe(log.samples.length * activeSlots(log).length)
    expect(rate.every((p) => p.dps >= 0)).toBe(true)
  })

  it('monster breakdown attributes your hits to monsters', () => {
    const [, log] = v2[0]!
    const rows = monsterBreakdown(log)
    expect(rows.length).toBe(log.monsters.length)
    expect(rows.reduce((s, r) => s + r.yourHits, 0)).toBe(log.hits.filter((h) => h.monster).length)
  })

  it('ranks hunters within a part and puts untagged hits under Unknown part', () => {
    const [, base] = v2[0]!
    const monster = base.monsters[0]!.id
    const log = {
      ...base,
      players: [
        { ...base.players[0]!, slot: 0, name: 'Local', isLocal: true },
        { slot: 1, name: 'Teammate', isLocal: false, damage: 500, dps: 0, percent: 0 },
      ],
      hits: [
        { t: 1, slot: 0, monster, damage: 100, crit: false, tenderized: false, attackId: 1, actionSet: 1, actionId: 1, part: 2, partName: 'Head' },
        { t: 2, slot: 0, monster, damage: 50, crit: false, tenderized: false, attackId: 1, actionSet: 1, actionId: 1, part: 2, partName: 'Head' },
        { t: 3, slot: 1, monster, damage: 80, crit: false, tenderized: false, attackId: 0, actionSet: 1, actionId: 1, estimated: true },
        { t: 4, slot: 0, monster, damage: 40, crit: false, tenderized: false, attackId: 1, actionSet: 1, actionId: 1, part: 0, partName: 'Tail' },
      ],
    }
    const rows = partBreakdown(log, monster)
    expect(rows[0]!.name).toBe('Head')
    expect(rows[0]!.damage).toBe(150)
    expect(rows[0]!.hunters[0]!.name).toBe('Local')
    expect(rows[0]!.hunters[0]!.damage).toBe(150)
    const unknown = rows.find((r) => r.part == null)!
    expect(unknown.damage).toBe(80)
    expect(unknown.hunters[0]!.name).toBe('Teammate')
    expect(hasPartData(log)).toBe(true)
  })

  it('finds trial personal bests from the index', () => {
    const index = parseIndex(readFileSync(join(SAMPLE, 'index.json'), 'utf8'))
    const bests = trialBests(index)
    expect(bests.length).toBe(1)
    expect(bests[0]!.weapon).toBe('DualBlades')
    expect(bests[0]!.durationSeconds).toBe(60)
  })

  it('rebuilds a cumulative curve from hits', () => {
    const [, trial] = logs.find(([, l]) => l.kind === 'trial')!
    const curve = cumulativeFromHits(trial.hits, 'run')
    expect(curve[0]!.damage).toBe(0)
    expect(curve[curve.length - 1]!.damage).toBe(trial.players[0]!.damage)
  })

  it('prettifies unknown action names', () => {
    expect(prettifyAction('WP_02::KIJIN_SLIDING_ON')).toBe('Kijin Sliding On')
    expect(moveDisplayName('Hammer', 'WP_04::SOMETHING_NEW')).toBe('Something New')
  })
})

it('interval DPS preserves bursts, idle time, uneven intervals and duplicate timestamps', () => {
  const log = { ...logs[0]![1], players: [{ ...logs[0]![1].players[0]!, slot: 0 }],
    samples: [[0, 0], [2, 100], [5, 100], [6, 400], [6, 400], [8, 500]]
      .map(([t, damage]) => ({ t: t!, damage: [damage!] })) }
  expect(intervalDps(log).map(p => [p.t, p.dps])).toEqual([[0, 0], [2, 50], [5, 0], [6, 300], [6, 0], [8, 50]])
})
