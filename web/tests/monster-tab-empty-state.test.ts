import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import * as ts from 'typescript'
import { normalizeLog } from '../src/data/parse'

/**
 * Verify the actual Monsters tab composition without mocking the browser:
 * a legacy log with no monsters must display both the table fallback and
 * the independent experimental gauge-history empty state.
 */
describe('Monsters tab empty state', () => {
  it('keeps GaugeHistory outside the monster-table Show guard', () => {
    const old = normalizeLog({ questName: 'Legacy/no monsters', players: [], monsters: [] })
    expect(old.monsters).toEqual([])
    expect(old.gaugeSamples).toEqual([])

    const source = ts.createSourceFile(
      'HuntDetail.tsx',
      readFileSync(new URL('../src/components/HuntDetail.tsx', import.meta.url), 'utf8'),
      ts.ScriptTarget.Latest,
      true,
      ts.ScriptKind.TSX,
    )
    const monsters = source.statements.find(
      (node): node is ts.FunctionDeclaration =>
        ts.isFunctionDeclaration(node) && node.name?.text === 'Monsters',
    )
    expect(monsters).toBeDefined()
    const selfClosing: ts.JsxSelfClosingElement[] = []
    const guards: ts.JsxElement[] = []
    function visit(node: ts.Node): void {
      if (ts.isJsxSelfClosingElement(node)) selfClosing.push(node)
      if (ts.isJsxElement(node) && node.openingElement.tagName.getText(source) === 'Show') guards.push(node)
      ts.forEachChild(node, visit)
    }
    visit(monsters!)

    const gauge = selfClosing.find(node => node.tagName.getText(source) === 'GaugeHistory')
    const table = selfClosing.find(node => node.tagName.getText(source) === 'TableView')
    const show = guards.find(node => node.openingElement.getText(source).includes('props.log.monsters.length'))
    expect(gauge).toBeDefined()
    expect(table).toBeDefined()
    expect(show).toBeDefined()
    expect(show!.openingElement.getText(source)).toContain('No large monsters were recorded for this log.')

    // The table is conditional, but the gauge panel is always mounted.
    function inside(node: ts.Node, ancestor: ts.Node): boolean {
      for (let parent = node.parent; parent; parent = parent.parent) {
        if (parent === ancestor) return true
        if (parent === monsters) break
      }
      return false
    }
    expect(inside(table!, show!)).toBe(true)
    expect(inside(gauge!, show!)).toBe(false)
  })
})
