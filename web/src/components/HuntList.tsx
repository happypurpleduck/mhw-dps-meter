import { For, createMemo, createSignal } from 'solid-js'
import { createColumnHelper } from '@tanstack/table-core'
import type { IndexEntry, LogKind } from '../schema/fightlog'
import { logStore } from '../data/store'
import { formatDuration, formatInt, formatShortDate } from '../data/analysis'
import { createSolidTable, type Features } from '../lib/table'
import { ResultBadge, TableView } from './TableView'

const col = createColumnHelper<Features, IndexEntry>()

const columns = [
  col.accessor('startedAt', {
    header: 'Date',
    sortFn: 'alphanumeric',
    meta: { class: 'w-28' },
    cell: (info) => <span class="whitespace-nowrap text-xs">{formatShortDate(info.getValue())}</span>,
  }),
  col.accessor('questName', {
    header: 'Hunt',
    sortFn: 'alphanumeric',
    cell: (info) => {
      const e = info.row.original
      return (
        <div class="min-w-0">
          <div class="font-medium truncate max-w-44">{e.questName}</div>
          <div class="text-xs text-base-content/60 truncate max-w-44">
            {e.kind === 'trial' ? e.weapon ?? 'trial' : [...e.monsters, ...e.players].join(' · ')}
          </div>
        </div>
      )
    },
  }),
  col.accessor('result', {
    header: 'Result',
    sortFn: 'alphanumeric',
    cell: (info) => <ResultBadge result={info.getValue()} />,
  }),
  col.accessor('durationSeconds', {
    header: 'Time',
    sortFn: 'basic',
    meta: { class: 'text-right' },
    cell: (info) => formatDuration(info.getValue()),
  }),
  col.accessor('totalDamage', {
    header: 'Dmg',
    sortFn: 'basic',
    meta: { class: 'text-right' },
    cell: (info) => formatInt(info.getValue()),
  }),
]

export function HuntList() {
  const [query, setQuery] = createSignal('')
  const [kind, setKind] = createSignal<LogKind | 'all'>('all')

  const filtered = createMemo(() => {
    const q = query().trim().toLowerCase()
    const k = kind()
    return logStore.entries().filter((e) => {
      if (k !== 'all' && e.kind !== k) return false
      if (!q) return true
      const hay = [e.questName, e.result, e.weapon ?? '', ...e.players, ...e.monsters].join(' ').toLowerCase()
      return hay.includes(q)
    })
  })

  const table = createSolidTable<IndexEntry>({
    data: filtered,
    columns,
    getRowId: (e) => e.file,
    initialSorting: [{ id: 'startedAt', desc: true }],
  })

  const selectedFile = () => {
    const v = logStore.view()
    return v.kind === 'hunt' ? v.file : undefined
  }

  const counts = createMemo(() => {
    const all = logStore.entries()
    return { all: all.length, quest: all.filter((e) => e.kind === 'quest').length, trial: all.filter((e) => e.kind === 'trial').length }
  })

  return (
    <div class="flex flex-col min-h-0 flex-1 max-w-full">
      <div class="p-3 flex gap-2 items-center border-b border-base-300">
        <input
          type="search"
          placeholder="Filter by quest, monster, hunter…"
          class="input input-sm input-bordered flex-1"
          value={query()}
          onInput={(e) => setQuery(e.currentTarget.value)}
        />
        <div role="tablist" class="tabs tabs-box tabs-xs">
          <For each={[['all', 'All'], ['quest', 'Quests'], ['trial', 'Trials']] as const}>
            {([value, label]) => (
              <button role="tab" class={['tab', { 'tab-active': kind() === value }]} onClick={() => setKind(value)}>
                {label} <span class="ml-1 opacity-60">{counts()[value]}</span>
              </button>
            )}
          </For>
        </div>
      </div>
      <div class="overflow-y-auto flex-1 min-h-0 lg:max-h-[calc(100vh-8.5rem)]">
        <TableView
          table={table}
          size="table-xs"
          zebra={false}
          onRowClick={(row) => logStore.selectHunt(row.original.file)}
          rowClass={(row) => ({ 'bg-primary/15': row.original.file === selectedFile() })}
          empty="No hunts match."
        />
      </div>
    </div>
  )
}
