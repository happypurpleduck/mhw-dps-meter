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
  it('does not hide per-monster quality in the metric-empty fallback', () => {
    const raw = readFileSync(new URL('../src/components/GaugeHistory.tsx', import.meta.url), 'utf8')
    const source = ts.createSourceFile('GaugeHistory.tsx', raw,
      ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX)
    const elements: ts.JsxElement[] = []
    const visit = (node: ts.Node): void => {
      if (ts.isJsxElement(node)) elements.push(node)
      ts.forEachChild(node, visit)
    }
    visit(source)
    const metricGuard = elements.find(node =>
      node.openingElement.getText(source).includes('when={options().length}'))
    const monsterGuard = elements.find(node =>
      node.openingElement.getText(source).includes('when={monsters().length}'))
    const quality = elements.find(node =>
      node.openingElement.getText(source).includes('aria-label="Monster gauge sample quality"'))
    const metricQuality = elements.find(node =>
      node.openingElement.getText(source).includes('aria-label="Selected gauge metric quality"'))
    expect(metricGuard).toBeDefined()
    expect(monsterGuard).toBeDefined()
    expect(quality).toBeDefined()
    expect(metricQuality).toBeDefined()
    expect(quality!.getText(source)).toContain('coverage().invalidTime')
    expect(quality!.getText(source)).toContain('coverage().sourceChanges')
    expect(quality!.getText(source)).toContain('coverage().clockBoundaries')
    expect(quality!.getText(source)).toContain('coverage().nonDefaultSource')
    expect(metricGuard!.getText(source)).toContain('No usable numeric gauge readings')
    expect(metricQuality!.getText(source)).toContain('coverage().missingMetric')
    let qualityInsideMonster = false
    for (let parent = quality!.parent; parent; parent = parent.parent) {
      if (parent === metricGuard) throw new Error('Monster quality hidden behind metric selector')
      if (parent === monsterGuard) qualityInsideMonster = true
    }
    expect(qualityInsideMonster).toBe(true)
    let metricInsideSelector = false
    for (let parent = metricQuality!.parent; parent; parent = parent.parent) {
      if (parent === metricGuard) metricInsideSelector = true
    }
    expect(metricInsideSelector).toBe(true)
  })

  it('uses the tested last-five presenter outside the chartable-metric guard', () => {
    const source = ts.createSourceFile('GaugeHistory.tsx',
      readFileSync(new URL('../src/components/GaugeHistory.tsx', import.meta.url), 'utf8'),
      ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX)
    const elements: ts.JsxElement[] = []
    const visit = (node: ts.Node): void => {
      if (ts.isJsxElement(node)) elements.push(node)
      ts.forEachChild(node, visit)
    }
    visit(source)
    const monsterGuard = elements.find(node =>
      node.openingElement.getText(source).includes('when={monsters().length}'))
    const metricGuard = elements.find(node =>
      node.openingElement.getText(source).includes('when={options().length}'))
    const markerList = elements.find(node =>
      node.openingElement.getText(source).includes('aria-label="Stored capture-quality markers"'))
    expect(monsterGuard).toBeDefined()
    expect(metricGuard).toBeDefined()
    expect(markerList).toBeDefined()
    expect(markerList!.getText(source)).toContain('recentGaugeQualityMarkers(qualityMarkers())')
    let withinMonster = false
    for (let parent = markerList!.parent; parent; parent = parent.parent) {
      if (parent === metricGuard) throw new Error('Capture markers hidden when no numeric metric exists')
      if (parent === monsterGuard) withinMonster = true
    }
    expect(withinMonster).toBe(true)
  })

})
