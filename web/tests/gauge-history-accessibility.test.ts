import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import * as ts from 'typescript'
import { normalizeLog } from '../src/data/parse'
import { availableGaugeMetrics } from '../src/data/gaugeHistory'

/** No DOM mock: inspect the actual Solid component's accessibility semantics. */
describe('gauge-history accessibility', () => {
  it('exposes selected metric state and a monster/metric-specific chart name', () => {
    const log = normalizeLog({ questName: 'Gauge history', players: [], gaugeSamples: [
      { t: 1, monster: 'm1', source: 'local-unverified', stamina: 40, maxStamina: 100,
        ailments: [{ id: 7, name: 'KO', buildup: 20, maxBuildup: 100 }] },
      { t: 3, monster: 'm1', source: 'local-unverified', stamina: 30, maxStamina: 100 },
      { t: 1, monster: 'm2', source: 'local-unverified', stamina: 70, maxStamina: 100 },
    ] })
    expect(availableGaugeMetrics(log.gaugeSamples, 'm1').map(m => m.label)).toEqual(['Stamina remaining', 'KO buildup'])
    expect(availableGaugeMetrics(log.gaugeSamples, 'm2').map(m => m.label)).toEqual(['Stamina remaining'])

    const source = ts.createSourceFile(
      'GaugeHistory.tsx',
      readFileSync(new URL('../src/components/GaugeHistory.tsx', import.meta.url), 'utf8'),
      ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX,
    )
    const elements: (ts.JsxOpeningElement | ts.JsxSelfClosingElement)[] = []
    function visit(node: ts.Node): void {
      if (ts.isJsxOpeningElement(node) || ts.isJsxSelfClosingElement(node)) elements.push(node)
      ts.forEachChild(node, visit)
    }
    visit(source)
    const attr = (el: ts.JsxOpeningElement | ts.JsxSelfClosingElement, name: string) =>
      el.attributes.properties.find(p => ts.isJsxAttribute(p) && p.name.getText(source) === name) as ts.JsxAttribute | undefined
    const group = elements.find(el => el.tagName.getText(source) === 'div'
      && attr(el, 'role')?.initializer?.getText(source) === '"group"'
      && attr(el, 'aria-label')?.initializer?.getText(source) === '"Gauge metric"')
    expect(group).toBeDefined()
    const metricButton = elements.find(el => el.tagName.getText(source) === 'button' && attr(el, 'aria-pressed'))
    expect(metricButton).toBeDefined()
    expect(attr(metricButton!, 'aria-pressed')?.initializer?.getText(source))
      .toContain('activeMetric()?.key === metric.key')
    const chart = elements.find(el => el.tagName.getText(source) === 'Chart')
    expect(chart).toBeDefined()
    const accessibleName = attr(chart!, 'ariaLabel')?.initializer?.getText(source) ?? ''
    expect(accessibleName).toContain('selectedMonsterName()')
    expect(accessibleName).toContain('monster()')
    expect(accessibleName).toContain('activeMetric()?.label')
    expect(accessibleName).toContain('local-unverified')
    // Remains a reactive JSX expression, not a static label.
    expect(ts.isJsxExpression(attr(chart!, 'ariaLabel')!.initializer!)).toBe(true)
  })
})
