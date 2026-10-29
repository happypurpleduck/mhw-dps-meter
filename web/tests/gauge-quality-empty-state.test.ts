import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import * as ts from 'typescript'
import { normalizeLog } from '../src/data/parse'

/** Source-level guard check: hunt-wide capture quality survives an empty gauge import. */
describe('GaugeHistory with no recognized samples', () => {
  it('renders the hunt-wide warning independently of monster availability', () => {
    const log = normalizeLog({ questName: 'Corrupt imported gauges', players: [], monsters: [],
      gaugeSamples: [null, {}, 42, { monster: '' }],
      gaugeSamplesDropped: 9, gaugeClockDiscontinuities: 2,
    })
    expect(log.gaugeSamples).toEqual([])
    expect(log.gaugeSamplesDropped).toBe(9)
    expect(log.gaugeClockDiscontinuities).toBe(2)

    const source = ts.createSourceFile(
      'GaugeHistory.tsx',
      readFileSync(new URL('../src/components/GaugeHistory.tsx', import.meta.url), 'utf8'),
      ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX,
    )
    const shows: ts.JsxElement[] = []
    const findShows = (node: ts.Node): void => {
      if (ts.isJsxElement(node) && node.openingElement.tagName.getText(source) === 'Show') {
        shows.push(node)
      }
      ts.forEachChild(node, findShows)
    }
    findShows(source)
    const monsterGuard = shows.find(node =>
      node.openingElement.getText(source).includes('when={monsters().length}'))
    const qualityGuard = shows.find(node =>
      node.openingElement.getText(source).includes('props.log.gaugeSamplesDropped > 0'))
    expect(monsterGuard).toBeDefined()
    expect(qualityGuard).toBeDefined()
    expect(qualityGuard!.getText(source)).toContain('Hunt-wide quality:')
    expect(qualityGuard!.getText(source)).toContain('props.log.gaugeClockDiscontinuities')

    for (let parent = qualityGuard!.parent; parent; parent = parent.parent) {
      expect(parent).not.toBe(monsterGuard)
    }
  })
})
