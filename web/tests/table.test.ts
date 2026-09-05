import { describe, expect, it } from 'vitest'
import { createRoot, createSignal, flush } from 'solid-js'
import { createColumnHelper } from '@tanstack/table-core'
import { createSolidTable, type Features } from '../src/lib/table'

interface Row {
  name: string
  damage: number
}

const col = createColumnHelper<Features, Row>()
const columns = [
  col.accessor('name', { header: 'Name', sortFn: 'alphanumeric' }),
  col.accessor('damage', { header: 'Damage', sortFn: 'basic' }),
]

describe('createSolidTable (Solid 2 binding over table-core)', () => {
  it('sorts by the initial sorting state and follows data changes', () => {
    // Solid 2 forbids signal writes inside an owned scope, so the table is created
    // inside a root and driven from outside it, the way event handlers do.
    const { t, setData, dispose } = createRoot((dispose) => {
      const [data, setData] = createSignal<Row[]>([
        { name: 'b', damage: 10 },
        { name: 'a', damage: 30 },
        { name: 'c', damage: 20 },
      ])
      const t = createSolidTable<Row>({ data, columns, initialSorting: [{ id: 'damage', desc: true }] })
      return { t, setData, dispose }
    })
    expect(t.rows().map((r) => r.original.name)).toEqual(['a', 'c', 'b'])

    setData((prev) => [...prev, { name: 'd', damage: 40 }])
    flush()
    expect(t.rows().map((r) => r.original.name)).toEqual(['d', 'a', 'c', 'b'])

    t.setSorting([{ id: 'name', desc: false }])
    flush()
    expect(t.rows().map((r) => r.original.name)).toEqual(['a', 'b', 'c', 'd'])

    // Header toggling goes through table-core's onSortingChange back into our signal.
    const header = t.headerGroups()[0]!.headers.find((h) => h.column.id === 'damage')!
    header.column.toggleSorting(true)
    flush()
    expect(t.sorting()).toEqual([{ id: 'damage', desc: true }])
    expect(t.rows()[0]!.original.name).toBe('d')
    dispose()
  })
})
