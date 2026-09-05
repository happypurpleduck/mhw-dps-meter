/**
 * Solid 2 bindings for @tanstack/table-core v9.
 *
 * The official @tanstack/solid-table adapter targets Solid 1 (it imports
 * `solid-js/web`, `batch`, `createComputed`, `observable`, all gone in 2.0),
 * so this file drives table-core's framework-agnostic store bindings from
 * Solid signals instead: our signals own `data` and `sorting`, a memo pushes
 * them into the table, and derived memos read the row model back.
 */
import { createComponent, createMemo, createSignal, untrack, type Accessor } from 'solid-js'
import type { JSX } from '@solidjs/web'
import {
  constructTable,
  createCoreRowModel,
  createSortedRowModel,
  rowSortingFeature,
  sortFn_alphanumeric,
  sortFn_basic,
  sortFn_datetime,
  tableFeatures,
  type ColumnDef,
  type Header,
  type HeaderGroup,
  type Row,
  type RowData,
  type SortingState,
  type Table,
  type Updater,
} from '@tanstack/table-core'
import { storeReactivityBindings } from '@tanstack/table-core/store-reactivity-bindings'

export const features = tableFeatures({
  rowSortingFeature,
  // Sort-function registry lives in the features object in v9; column defs
  // reference these keys via `sortFn`.
  sortFns: { alphanumeric: sortFn_alphanumeric, basic: sortFn_basic, datetime: sortFn_datetime },
  // Row models are features too in v9.
  coreRowModel: createCoreRowModel(),
  sortedRowModel: createSortedRowModel(),
  // Framework-agnostic TanStack Store atoms; Solid signals drive them from createSolidTable.
  coreReactivityFeature: storeReactivityBindings(),
})
export type Features = typeof features

export type Column<TData extends RowData, TValue = unknown> = ColumnDef<Features, TData, TValue>

export interface SolidTable<TData extends RowData> {
  table: Table<Features, TData>
  rows: Accessor<Row<Features, TData>[]>
  headerGroups: Accessor<HeaderGroup<Features, TData>[]>
  sorting: Accessor<SortingState>
  setSorting: (next: SortingState) => void
}

export interface SolidTableOptions<TData extends RowData> {
  data: Accessor<TData[]>
  columns: Column<TData, any>[]
  initialSorting?: SortingState
  getRowId?: (row: TData, index: number) => string
}

export function createSolidTable<TData extends RowData>(options: SolidTableOptions<TData>): SolidTable<TData> {
  const [sorting, setSorting] = createSignal<SortingState>(options.initialSorting ?? [])

  const table = constructTable<Features, TData>({
    features,
    columns: options.columns,
    data: untrack(options.data),
    getRowId: options.getRowId,
    enableSortingRemoval: true,
    state: { sorting: untrack(sorting) },
    onSortingChange: (updater: Updater<SortingState>) =>
      setSorting((prev) => (typeof updater === 'function' ? updater(prev) : updater)),
  })

  // Push our signals into the table whenever they change; readers below depend
  // on this memo so they always see a table whose options are current.
  // Returns a fresh object each run so dependents re-run even though the table
  // instance itself never changes.
  const synced = createMemo(() => {
    const data = options.data()
    const s = sorting()
    table.setOptions((prev) => ({ ...prev, data, state: { ...prev.state, sorting: s } }))
    return { table, data, sorting: s }
  })

  const rows = createMemo(() => synced().table.getRowModel().rows)
  const headerGroups = createMemo(() => synced().table.getHeaderGroups())

  return { table, rows, headerGroups, sorting, setSorting }
}

/** Renders a header/cell/footer template: a component gets its context as props, anything else is emitted as-is. */
export function flexRender<TProps extends object>(template: unknown, props: TProps): JSX.Element {
  if (template === null || template === undefined) return null
  if (typeof template === 'function') return createComponent(template as (p: TProps) => JSX.Element, props)
  return template as JSX.Element
}

export function sortIndicator<TData extends RowData>(header: Header<Features, TData, unknown>): string {
  const dir = header.column.getIsSorted()
  return dir === 'asc' ? ' ▲' : dir === 'desc' ? ' ▼' : ''
}
