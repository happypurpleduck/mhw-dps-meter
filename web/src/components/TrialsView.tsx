import { weaponDisplayName } from '../data/names'
import { WeaponIcon, WeaponLabel } from './WeaponIcon'
import { For, Loading, Show, createMemo, createSignal } from 'solid-js'
import { createColumnHelper } from '@tanstack/table-core'
import type { FightLog, IndexEntry } from '../schema/fightlog'
import { logStore } from '../data/store'
import { cumulativeFromHits, formatDate, formatInt, hitStats, moveBreakdown, pct, trialBests, type TrialBest } from '../data/analysis'
import { moveDisplayName } from '../moves/names'
import { createSolidTable, type Features } from '../lib/table'
import { Chart } from '../lib/Chart'
import { compareCurvesChart } from '../charts/definitions'
import { TableView } from './TableView'

const COMPARE_COLORS = ['#52b8ff', '#ff9e2e']

const bestCol = createColumnHelper<Features, TrialBest>()
const bestColumns = [
  bestCol.accessor('weapon', { header: 'Weapon', sortFn: 'alphanumeric', cell: (info) => <WeaponLabel weapon={info.getValue()} /> }),
  bestCol.accessor('durationSeconds', { header: 'Window', sortFn: 'basic', meta: { class: 'text-right' }, cell: (info) => `${info.getValue()}s` }),
  bestCol.accessor((b): number => b.best.totalDamage, { id: 'damage', header: 'Best damage', sortFn: 'basic', meta: { class: 'text-right' }, cell: (info) => <span class="font-semibold text-primary">{formatInt(info.getValue())}</span> }),
  bestCol.accessor((b): number => b.best.totalDamage / Math.max(1, b.durationSeconds), { id: 'dps', header: 'DPS', sortFn: 'basic', meta: { class: 'text-right' }, cell: (info) => info.getValue().toFixed(1) }),
  bestCol.accessor('attempts', { header: 'Attempts', sortFn: 'basic', meta: { class: 'text-right' } }),
  bestCol.accessor((b): string => b.best.startedAt, { id: 'when', header: 'Set on', sortFn: 'alphanumeric', cell: (info) => formatDate(info.getValue()) }),
]

const trialCol = createColumnHelper<Features, IndexEntry>()

export function TrialsView() {
  const trials = createMemo(() => logStore.entries().filter((e) => e.kind === 'trial'))
  const bests = createMemo(() => trialBests(logStore.entries()))
  const [compare, setCompare] = createSignal<string[]>([])

  const toggleCompare = (file: string) =>
    setCompare((prev) => (prev.includes(file) ? prev.filter((f) => f !== file) : [...prev.slice(-1), file]))

  const trialColumns = [
    trialCol.display({
      id: 'pick',
      header: 'Compare',
      cell: (info) => (
        <input
          type="checkbox"
          class="checkbox checkbox-sm"
          checked={compare().includes(info.row.original.file)}
          onChange={() => toggleCompare(info.row.original.file)}
        />
      ),
    }),
    trialCol.accessor('startedAt', { header: 'Date', sortFn: 'alphanumeric', cell: (info) => formatDate(info.getValue()) }),
    trialCol.accessor('weapon', { header: 'Weapon', sortFn: 'alphanumeric', cell: (info) => <WeaponLabel weapon={info.getValue()} /> }),
    trialCol.accessor('durationSeconds', { header: 'Window', sortFn: 'basic', meta: { class: 'text-right' }, cell: (info) => `${Math.round(info.getValue())}s` }),
    trialCol.accessor('totalDamage', { header: 'Damage', sortFn: 'basic', meta: { class: 'text-right' }, cell: (info) => formatInt(info.getValue()) }),
    trialCol.accessor((e): number => e.totalDamage / Math.max(1, e.durationSeconds), { id: 'dps', header: 'DPS', sortFn: 'basic', meta: { class: 'text-right' }, cell: (info) => info.getValue().toFixed(1) }),
    trialCol.display({
      id: 'open',
      header: '',
      cell: (info) => (
        <button class="btn btn-xs btn-ghost" onClick={() => logStore.selectHunt(info.row.original.file)}>
          open
        </button>
      ),
    }),
  ]

  const bestTable = createSolidTable<TrialBest>({ data: bests, columns: bestColumns, getRowId: (b) => `${b.weapon}|${b.durationSeconds}` })
  const trialTable = createSolidTable<IndexEntry>({ data: trials, columns: trialColumns, getRowId: (e) => e.file, initialSorting: [{ id: 'startedAt', desc: true }] })

  // Async memo: loads both selected trials; readers below wait inside <Loading>.
  const compared = createMemo(async () => {
    const files = compare()
    const s = logStore.source()
    if (!s || files.length < 2) return undefined
    return Promise.all(files.map((f) => s.loadLog(f)))
  })

  return (
    <div class="flex flex-col gap-6">
      <div>
        <h1 class="text-2xl font-semibold">Time trials</h1>
        <p class="text-sm text-base-content/60">
          Training-area runs recorded by the plugin (F8). Personal bests are per weapon and window, exactly as the F9 panel shows them.
        </p>
      </div>
      <Show when={trials().length} fallback={<div class="text-base-content/60">No trials in this source yet. Arm one with F8 in the training area.</div>}>
        <section>
          <h2 class="text-lg font-medium mb-2">Personal bests</h2>
          <TableView table={bestTable} />
        </section>
        <section>
          <h2 class="text-lg font-medium mb-2">All runs <span class="text-sm text-base-content/50">tick two to compare</span></h2>
          <TableView table={trialTable} />
        </section>
        <Show when={compare().length === 2}>
          <Loading fallback={<div class="flex justify-center p-6"><span class="loading loading-spinner" /></div>}>
            <Show when={compared() as unknown as FightLog[] | undefined}>
              {(logs) => <Comparison logs={logs()} />}
            </Show>
          </Loading>
        </Show>
      </Show>
    </div>
  )
}

function Comparison(props: { logs: FightLog[] }) {
  const series = () =>
    props.logs.map((log, i) => ({
      label: `${formatDate(log.startedAt)} · ${formatInt(log.players[0]?.damage ?? 0)}`,
      color: COMPARE_COLORS[i] ?? '#ccc',
      points: cumulativeFromHits(log.hits, `run ${i + 1}`),
    }))
  return (
    <section class="card bg-base-200">
      <div class="card-body p-4 gap-4">
        <h2 class="card-title text-base">Comparison</h2>
        <div class="flex flex-wrap gap-4">
          <For each={props.logs}>
            {(log, i) => {
              const stats = () => hitStats(log.hits)
              return (
                <div class="flex items-center gap-2 text-sm">
                  <span class="inline-block size-3 rounded-full" style={{ background: COMPARE_COLORS[i()] }} />
                  <span>{formatDate(log.startedAt)}</span>
                  <span class="badge badge-sm gap-1.5 pl-1">
                    <WeaponIcon weapon={log.players[0]?.weapon} size="sm" />
                    {weaponDisplayName(log.players[0]?.weapon)}
                  </span>
                  <span class="font-semibold">{formatInt(log.players[0]?.damage ?? 0)}</span>
                  <span class="text-base-content/60">{stats().hits} hits · crit {pct(stats().critRate, 0)}</span>
                </div>
              )
            }}
          </For>
        </div>
        <Chart definition={compareCurvesChart(series())} height={300} ariaLabel="Cumulative damage of the compared trials" />
        <div class="grid md:grid-cols-2 gap-4">
          <For each={props.logs}>
            {(log, i) => {
              const weapon = log.players[0]?.weapon ?? null
              const rows = moveBreakdown(log.hits, (k) => moveDisplayName(weapon, k)).slice(0, 8)
              return (
                <div>
                  <h3 class="text-sm font-medium mb-1" style={{ color: COMPARE_COLORS[i()] }}>Run {i() + 1}: top moves</h3>
                  <table class="table table-xs">
                    <thead><tr><th>Move</th><th class="text-right">Damage</th><th class="text-right">Share</th><th class="text-right">Hits</th></tr></thead>
                    <tbody>
                      <For each={rows}>
                        {(r) => (
                          <tr><td>{r.name}</td><td class="text-right">{formatInt(r.damage)}</td><td class="text-right">{pct(r.share, 0)}</td><td class="text-right">{r.hits}</td></tr>
                        )}
                      </For>
                    </tbody>
                  </table>
                </div>
              )
            }}
          </For>
        </div>
      </div>
    </section>
  )
}
