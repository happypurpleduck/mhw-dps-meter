import { describe, expect, it } from 'vitest'
import { readFileSync, readdirSync } from 'node:fs'
import { join } from 'node:path'
import { parseIndex, parseLog, indexEntryFromLog } from '../src/data/parse'
import {
  activeSlots,
  intervalDps,
  cumulativeFromHits,
  damageCurves,
  hitStats,
  monsterBreakdown,
  moveBreakdown,
  rollingDps,
  trialBests,
} from '../src/data/analysis'
import { moveDisplayName, prettifyAction } from '../src/moves/names'

const SAMPLE = join(__dirname, '..', 'public', 'sample')
const files = readdirSync(SAMPLE).filter((f) => f.endsWith('.json') && f !== 'index.json')
const logs = files.map((f) => [f, parseLog(readFileSync(join(SAMPLE, f), 'utf8'))] as const)
const v2 = logs.filter(([, l]) => l.schemaVersion === 2 && l.kind === 'quest')

describe('parse', () => {
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
