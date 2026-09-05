import { For, Show } from 'solid-js'
import type { Row, RowData } from '@tanstack/table-core'
import { flexRender, sortIndicator, type Features, type SolidTable } from '../lib/table'

export interface TableViewProps<TData extends RowData> {
  table: SolidTable<TData>
  class?: string
  /** daisyUI size class: table-xs | table-sm | table-md */
  size?: string
  zebra?: boolean
  onRowClick?: (row: Row<Features, TData>) => void
  rowClass?: (row: Row<Features, TData>) => string | Record<string, boolean> | undefined
  empty?: string
}

/** Renders a TanStack table with daisyUI table classes; header clicks sort. */
export function TableView<TData extends RowData>(props: TableViewProps<TData>) {
  return (
    <div class={['overflow-x-auto', props.class]}>
      <table class={['table', props.size ?? 'table-sm', { 'table-zebra': props.zebra ?? true }]}>
        <thead>
          <For each={props.table.headerGroups()}>
            {(group) => (
              <tr>
                <For each={group.headers}>
                  {(header) => (
                    <th
                      colspan={header.colSpan}
                      class={[
                        'select-none whitespace-nowrap',
                        { 'cursor-pointer hover:text-primary': header.column.getCanSort() },
                        (header.column.columnDef.meta as { class?: string } | undefined)?.class,
                      ]}
                      onClick={header.column.getToggleSortingHandler()}
                    >
                      <Show when={!header.isPlaceholder}>
                        {flexRender(header.column.columnDef.header, header.getContext())}
                        <span class="text-primary">{sortIndicator(header as never)}</span>
                      </Show>
                    </th>
                  )}
                </For>
              </tr>
            )}
          </For>
        </thead>
        <tbody>
          <For each={props.table.rows()} fallback={<tr><td colspan={99} class="text-center text-base-content/50 py-6">{props.empty ?? 'Nothing to show.'}</td></tr>}>
            {(row) => (
              <tr
                class={[{ 'cursor-pointer hover:bg-base-300/60': !!props.onRowClick }, props.rowClass?.(row)]}
                onClick={() => props.onRowClick?.(row)}
              >
                <For each={row.getAllCells()}>
                  {(cell) => (
                    <td class={(cell.column.columnDef.meta as { class?: string } | undefined)?.class}>
                      {flexRender(cell.column.columnDef.cell ?? ((ctx: { getValue(): unknown }) => String(ctx.getValue() ?? '')), cell.getContext())}
                    </td>
                  )}
                </For>
              </tr>
            )}
          </For>
        </tbody>
      </table>
    </div>
  )
}

export function StatTile(props: { title: string; value: string; desc?: string; accent?: string }) {
  return (
    <div class="stat py-3 px-4">
      <div class="stat-title text-xs">{props.title}</div>
      <div class={['stat-value text-2xl', props.accent]}>{props.value}</div>
      <Show when={props.desc}>
        <div class="stat-desc">{props.desc}</div>
      </Show>
    </div>
  )
}

export function ResultBadge(props: { result: string }) {
  const cls = () =>
    props.result === 'complete'
      ? 'badge-success'
      : props.result === 'fail'
        ? 'badge-error'
        : props.result === 'trial'
          ? 'badge-info'
          : 'badge-ghost'
  return <span class={['badge badge-sm', cls()]}>{props.result}</span>
}
