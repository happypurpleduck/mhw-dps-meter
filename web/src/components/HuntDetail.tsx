import { weaponDisplayName, stageDisplayName } from '../data/names'
import { Parts } from './Parts'
import { WeaponIcon } from './WeaponIcon'
import { For, Match, Show, Switch, createMemo, createSignal } from 'solid-js'
import { createColumnHelper } from '@tanstack/table-core'
import type { FightLog, FightLogEvent, FightLogPlayer } from '../schema/fightlog'
import { slotColor } from '../schema/fightlog'
import { logStore } from '../data/store'
import {
  formatDate,
  formatDuration,
  formatInt,
  hasPartData,
  hitStats,
  hitsForSlot,
  isEstimated,
  localPlayer,
  slotsWithHits,
  monsterBreakdown,
  moveBreakdown,
  partBreakdown,
  pct,
  totalDamage,
  type MonsterRow,
  type MoveRow,
  type PartRow,
} from '../data/analysis'
import { moveDisplayName } from '../moves/names'
import { createSolidTable, type Features } from '../lib/table'
import { Chart } from '../lib/Chart'
import { damageCurveChart, dpsCurveChart, eventMarkers, movesBarChart } from '../charts/definitions'
import { ResultBadge, StatTile, TableView } from './TableView'

type Tab = 'overview' | 'moves' | 'parts' | 'monsters' | 'timeline' | 'raw'

export function HuntDetail() {
  // Async memo: inside <Loading> this read resolves to the loaded log.
  const log = () => logStore.selectedLog() as unknown as FightLog | undefined
  const initialTab = new URLSearchParams(location.search).get('tab') as Tab | null
  const tabs = ['overview', 'moves', 'parts', 'monsters', 'timeline', 'raw'] as const
  const [tab, setTab] = createSignal<Tab>(initialTab && tabs.includes(initialTab) ? initialTab : 'overview')

  return (
    <Show when={log()} fallback={<div class="text-base-content/60">No hunt selected.</div>}>
      {(l) => (
        <div class="flex flex-col gap-4">
          <Header log={l()} />
          <div role="tablist" class="tabs tabs-box w-fit">
            <For each={[['overview', 'Overview'], ['moves', 'Moves'], ['parts', 'Parts'], ['monsters', 'Monsters'], ['timeline', 'Timeline'], ['raw', 'Raw']] as const}>
              {([value, label]) => (
                <button role="tab" class={['tab', { 'tab-active': tab() === value }]} onClick={() => setTab(value)}>
                  {label}
                </button>
              )}
            </For>
          </div>
          <Switch>
            <Match when={tab() === 'overview'}><Overview log={l()} /></Match>
            <Match when={tab() === 'moves'}><Moves log={l()} /></Match>
            <Match when={tab() === 'parts'}><Parts log={l()} /></Match>
            <Match when={tab() === 'monsters'}><Monsters log={l()} /></Match>
            <Match when={tab() === 'timeline'}><Timeline log={l()} /></Match>
            <Match when={tab() === 'raw'}><Raw log={l()} /></Match>
          </Switch>
        </div>
      )}
    </Show>
  )
}

function Header(props: { log: FightLog }) {
  const me = () => localPlayer(props.log)
  const stats = () => hitStats(props.log.hits.filter((h) => h.slot === me()?.slot && !h.estimated))
  const total = () => totalDamage(props.log)
  const duration = () => Math.max(props.log.durationSeconds, 1)
  return (
    <div class="flex flex-col gap-3">
      <div class="flex flex-wrap items-baseline gap-3">
        <h1 class="text-2xl font-semibold">{props.log.questName}</h1>
        <ResultBadge result={props.log.result} />
        <Show when={props.log.kind === 'trial'}><span class="badge badge-info badge-outline badge-sm">time trial</span></Show>
        <span class="text-sm text-base-content/60">
          {formatDate(props.log.startedAt)} · {stageDisplayName(props.log.stageId, props.log.stage)} · schema {props.log.schemaVersion}
        </span>
        <Show when={props.log.rewards}>
          {(r) => (
            <span class="text-sm text-base-content/60">
              rewards {formatInt(r().zenny)}z · {formatInt(r().hunterRankPoints)} HRP · {r().stars}★
            </span>
          )}
        </Show>
      </div>
      <div class="stats stats-vertical sm:stats-horizontal bg-base-200 rounded-box shadow-sm flex-wrap">
        <StatTile title="Total damage" value={formatInt(total())} desc={`${props.log.players.length} hunter${props.log.players.length === 1 ? '' : 's'}`} />
        <StatTile title="Party DPS" value={(total() / duration()).toFixed(1)} desc={`over ${formatDuration(props.log.durationSeconds)}`} />
        <Show when={me()}>
          {(p) => (
            <>
              <StatTile title="Your damage" value={formatInt(p().damage)} desc={`${p().percent.toFixed(1)}% of party · ${weaponDisplayName(p().weapon)}`} accent="text-primary" />
              <StatTile title="Your hits" value={String(stats().hits)} desc={stats().hits ? `crit ${pct(stats().critRate)} · avg ${stats().avg.toFixed(0)} · max ${stats().max}` : 'no per-hit data (schema 1)'} />
              <Show when={stats().hits > 0}>
                <StatTile title="Active DPS" value={stats().activeDps.toFixed(1)} desc={`first→last hit ${formatDuration(stats().activeSeconds)}`} />
              </Show>
            </>
          )}
        </Show>
      </div>
    </div>
  )
}

// ---- Overview ----------------------------------------------------------------

const playerCol = createColumnHelper<Features, FightLogPlayer>()
const playerColumns = [
  playerCol.accessor('name', {
    header: 'Hunter',
    sortFn: 'alphanumeric',
    cell: (info) => (
      <span class="flex items-center gap-2 min-w-0">
        <span class="inline-block size-3 rounded-full shrink-0" style={{ background: slotColor(info.row.original.slot) }} />
        <WeaponIcon weapon={info.row.original.weapon} />
        <span class="font-medium truncate">{info.getValue()}</span>
        <Show when={info.row.original.isLocal}><span class="badge badge-xs badge-primary shrink-0">you</span></Show>
      </span>
    ),
  }),
  playerCol.accessor('damage', { header: 'Damage', sortFn: 'basic', meta: { class: 'text-right whitespace-nowrap' }, cell: (info) => formatInt(info.getValue()) }),
  playerCol.accessor('dps', { header: 'DPS', sortFn: 'basic', meta: { class: 'text-right w-16 whitespace-nowrap' }, cell: (info) => info.getValue().toFixed(1) }),
  playerCol.accessor((row) => row.carts ?? 0, {
    id: 'carts',
    header: 'Carts',
    sortFn: 'basic',
    meta: { class: 'text-right w-16 whitespace-nowrap' },
    cell: (info) => (info.getValue() > 0 ? String(info.getValue()) : '—'),
  }),
  playerCol.accessor('percent', {
    header: 'Share',
    sortFn: 'basic',
    meta: { class: 'w-52' },
    cell: (info) => (
      <div class="flex items-center gap-2 min-w-0">
        <progress class="progress progress-primary flex-1 min-w-16 max-w-28" value={info.getValue()} max="100" />
        <span class="text-xs shrink-0 tabular-nums">{info.getValue().toFixed(1)}%</span>
      </div>
    ),
  }),
]

function Overview(props: { log: FightLog }) {
  const players = () => props.log.players
  const table = createSolidTable<FightLogPlayer>({ data: players, columns: playerColumns, initialSorting: [{ id: 'damage', desc: true }] })
  const [windowSeconds, setWindowSeconds] = createSignal(20)
  const hasSamples = () => props.log.samples.length > 1
  return (
    <div class="flex flex-col gap-4">
      <TableView table={table} />
      <Show when={hasSamples()} fallback={<div class="text-sm text-base-content/60">This log has no damage samples.</div>}>
        <section class="card bg-base-200">
          <div class="card-body p-4 gap-2">
            <h3 class="card-title text-base">Cumulative damage</h3>
            <p class="text-xs text-base-content/60">Dashed lines: red = large monster death, orange = enrage, yellow = hunter cart.</p>
            <Chart definition={damageCurveChart(props.log, eventMarkers(props.log))} height={320} ariaLabel="Cumulative damage per hunter" />
          </div>
        </section>
        <section class="card bg-base-200">
          <div class="card-body p-4 gap-2">
            <h3 class="card-title text-base">DPS</h3>
            <p class="text-xs text-base-content/60">Damage gained / elapsed time between consecutive samples. No rolling window; party samples are usually 2s apart.</p>
            <Chart definition={dpsCurveChart(props.log, null)} height={260} ariaLabel="DPS per sample interval per hunter" />
          </div>
        </section>
        <section class="card bg-base-200">
          <div class="card-body p-4 gap-2">
            <div class="flex items-center justify-between">
              <h3 class="card-title text-base">Rolling DPS</h3>
              <div class="join">
                <For each={[10, 20, 30, 60]}>
                  {(w) => (
                    <button class={['btn btn-xs join-item', { 'btn-active': windowSeconds() === w }]} onClick={() => setWindowSeconds(w)}>
                      {w}s
                    </button>
                  )}
                </For>
              </div>
            </div>
            <Chart definition={dpsCurveChart(props.log, windowSeconds())} height={260} ariaLabel="Rolling DPS per hunter" />
          </div>
        </section>
      </Show>
    </div>
  )
}

// ---- Moves -------------------------------------------------------------------

const moveCol = createColumnHelper<Features, MoveRow>()
const moveColumns = [
  moveCol.accessor('name', {
    header: 'Move',
    sortFn: 'alphanumeric',
    cell: (info) => (
      <div class="min-w-0 overflow-hidden">
        <div class="font-medium truncate">{info.getValue()}</div>
        <div class="text-[10px] text-base-content/50 font-mono truncate">{info.row.original.key}</div>
      </div>
    ),
  }),
  moveCol.accessor('damage', { header: 'Damage', sortFn: 'basic', meta: { class: 'text-right whitespace-nowrap' }, cell: (info) => formatInt(info.getValue()) }),
  moveCol.accessor('share', {
    header: 'Share',
    sortFn: 'basic',
    meta: { class: 'w-44' },
    cell: (info) => (
      <div class="flex items-center gap-2 min-w-0">
        <progress class="progress progress-secondary flex-1 min-w-12 max-w-24" value={info.getValue() * 100} max="100" />
        <span class="text-xs shrink-0 tabular-nums">{pct(info.getValue())}</span>
      </div>
    ),
  }),
  moveCol.accessor('hits', { header: 'Hits', sortFn: 'basic', meta: { class: 'text-right' } }),
  moveCol.accessor('critRate', { header: 'Crit', sortFn: 'basic', meta: { class: 'text-right' }, cell: (info) => pct(info.getValue(), 0) }),
  moveCol.accessor('avg', { header: 'Avg', sortFn: 'basic', meta: { class: 'text-right' }, cell: (info) => info.getValue().toFixed(1) }),
  moveCol.accessor('max', { header: 'Max', sortFn: 'basic', meta: { class: 'text-right' } }),
  moveCol.accessor('tenderized', { header: 'Tenderized', sortFn: 'basic', meta: { class: 'text-right' } }),
]

function Moves(props: { log: FightLog }) {
  const [hideCommon, setHideCommon] = createSignal(false)
  const slots = () => slotsWithHits(props.log)
  const [slot, setSlot] = createSignal<number | undefined>(undefined)
  const activeSlot = () => slot() ?? slots()[0] ?? 0
  const player = () => props.log.players.find((p) => p.slot === activeSlot())
  const weapon = () => player()?.weapon ?? null
  const hits = () => hitsForSlot(props.log, activeSlot())
  const estimated = () => isEstimated(hits())
  const rows = createMemo(() => {
    const all = moveBreakdown(hits(), (key) => moveDisplayName(weapon(), key))
    return hideCommon() ? all.filter((r) => !r.isCommon) : all
  })
  const table = createSolidTable<MoveRow>({ data: rows, columns: moveColumns, getRowId: (r) => r.key, initialSorting: [{ id: 'damage', desc: true }] })
  return (
    <Show when={props.log.hits.length} fallback={<div class="text-sm text-base-content/60">This log has no per-hit data. Logs written by plugin 0.4.0 or later include every hit of the local hunter.</div>}>
      <div class="flex flex-col gap-4">
        <Show when={slots().length > 1}>
          <div role="tablist" class="tabs tabs-box w-fit">
            <For each={slots()}>
              {(s) => {
                const p = props.log.players.find((x) => x.slot === s)
                return (
                  <button role="tab" class={['tab gap-2', { 'tab-active': activeSlot() === s }]} onClick={() => setSlot(s)}>
                    <span class="inline-block size-2.5 rounded-full shrink-0" style={{ background: slotColor(s) }} />
                    <WeaponIcon weapon={p?.weapon} size="sm" />
                    {p?.name ?? `Slot ${s + 1}`}
                    <Show when={isEstimated(hitsForSlot(props.log, s))}><span class="badge badge-xs badge-warning badge-outline">est.</span></Show>
                  </button>
                )
              }}
            </For>
          </div>
        </Show>
        <div class="flex flex-wrap items-center gap-4 text-sm">
          <span class="text-base-content/70">
            <Show
              when={!estimated()}
              fallback={
                <>
                  <b>Estimated</b> per-move damage for {player()?.name ?? 'teammate'} ({weaponDisplayName(weapon())}): {hits().length} award-table
                  increments credited to the move they were performing. Crit and tenderize are unknown for teammates.
                </>
              }
            >
              Per-move breakdown of <b>{player()?.isLocal ? 'your' : `${player()?.name}'s`}</b> {hits().length} hits ({weaponDisplayName(weapon())}).
            </Show>
          </span>
          <label class="label cursor-pointer gap-2">
            <input type="checkbox" class="toggle toggle-sm" checked={hideCommon()} onChange={(e) => setHideCommon(e.currentTarget.checked)} />
            <span class="label-text">Hide Common:: actions (hits that landed after the move ended)</span>
          </label>
        </div>
        <section class="card bg-base-200">
          <div class="card-body p-4">
            <Chart definition={movesBarChart(rows(), slotColor(activeSlot()))} height={Math.max(160, Math.min(rows().length, 15) * 26 + 60)} ariaLabel="Damage per move" />
          </div>
        </section>
        <TableView table={table} />
      </div>
    </Show>
  )
}

// ---- Parts ---------------------------------------------------------------------

const partCol = createColumnHelper<Features, PartRow>()
const partColumns = [
  partCol.accessor('name', {
    header: 'Part',
    sortFn: 'alphanumeric',
    cell: (info) => (
      <div class="min-w-0">
        <div class="font-medium">{info.getValue()}</div>
        <Show when={info.row.original.part != null}>
          <div class="text-[10px] text-base-content/50 font-mono">id {info.row.original.part}</div>
        </Show>
      </div>
    ),
  }),
  partCol.accessor('damage', { header: 'Damage', sortFn: 'basic', meta: { class: 'text-right whitespace-nowrap' }, cell: (info) => formatInt(info.getValue()) }),
  partCol.accessor('share', {
    header: 'Share',
    sortFn: 'basic',
    meta: { class: 'w-44' },
    cell: (info) => (
      <div class="flex items-center gap-2 min-w-0">
        <progress class="progress progress-secondary flex-1 min-w-12 max-w-24" value={info.getValue() * 100} max="100" />
        <span class="text-xs shrink-0 tabular-nums">{pct(info.getValue())}</span>
      </div>
    ),
  }),
  partCol.accessor('hits', { header: 'Hits', sortFn: 'basic', meta: { class: 'text-right' } }),
  partCol.accessor((r) => r.hunters[0]?.name ?? '', {
    id: 'topHunter',
    header: 'Most damage',
    sortFn: 'alphanumeric',
    cell: (info) => {
      const top = info.row.original.hunters[0]
      if (!top) return <span class="opacity-40">—</span>
      return (
        <div class="flex items-center gap-2 min-w-0">
          <span class="inline-block size-2.5 rounded-full shrink-0" style={{ background: slotColor(top.slot) }} />
          <span class="truncate">{top.name}</span>
          <span class="text-xs text-base-content/60 tabular-nums shrink-0">{formatInt(top.damage)}</span>
        </div>
      )
    },
  }),
]

function Parts(props: { log: FightLog }) {
  const monsters = () => props.log.monsters
  const [monsterId, setMonsterId] = createSignal<string | undefined>(undefined)
  const activeMonster = () => monsterId() ?? monsters()[0]?.id
  const rows = createMemo(() => partBreakdown(props.log, activeMonster()))
  const [selectedPart, setSelectedPart] = createSignal<number | null | undefined>(undefined)
  const selected = createMemo(() => {
    const id = selectedPart()
    if (id === undefined) return rows()[0]
    return rows().find((r) => r.part === id) ?? rows()[0]
  })
  const table = createSolidTable<PartRow>({
    data: rows,
    columns: partColumns,
    getRowId: (r) => (r.part == null ? 'unknown' : String(r.part)),
    initialSorting: [{ id: 'damage', desc: true }],
  })
  return (
    <Show
      when={props.log.hits.some((h) => h.monster)}
      fallback={<div class="text-sm text-base-content/60">This log has no monster-targeted hits to group by part.</div>}
    >
      <div class="flex flex-col gap-4">
        <Show when={monsters().length > 1}>
          <div role="tablist" class="tabs tabs-box w-fit">
            <For each={monsters()}>
              {(m) => (
                <button role="tab" class={['tab', { 'tab-active': activeMonster() === m.id }]} onClick={() => { setMonsterId(m.id); setSelectedPart(undefined) }}>
                  {m.name}
                </button>
              )}
            </For>
          </div>
        </Show>
        <p class="text-sm text-base-content/70">
          <Show
            when={hasPartData(props.log)}
            fallback={
              <>
                No part tags in this log yet (plugin builds that resolve parts write <code class="text-xs">part</code> on
                your exact hits). Untagged and teammate rows appear under Unknown part — party award totals are not split by
                part.
              </>
            }
          >
            Damage by monster part. Part tags come from your exact hits; teammate award deltas have no part, so ranking
            within a part is only meaningful for hunters with tagged hits (usually you).
          </Show>
        </p>
        <section class="card bg-base-200">
          <div class="card-body p-4">
            <Chart definition={movesBarChart(rows(), '#f26bb8')} height={Math.max(160, Math.min(rows().length, 15) * 26 + 60)} ariaLabel="Damage per monster part" />
          </div>
        </section>
        <div class="grid gap-4 lg:grid-cols-[1fr_minmax(16rem,20rem)]">
          <TableView
            table={table}
            onRowClick={(row) => setSelectedPart(row.original.part)}
            rowClass={(row) => ({ 'bg-base-300/50': selected()?.part === row.original.part })}
          />
          <Show when={selected()}>
            {(part) => (
              <section class="card bg-base-200 h-fit">
                <div class="card-body p-4 gap-3">
                  <h3 class="font-medium">{part().name}</h3>
                  <p class="text-xs text-base-content/60">Hunters ranked by damage to this part</p>
                  <ul class="flex flex-col gap-2">
                    <For each={part().hunters} fallback={<li class="text-sm opacity-50">No tagged hits</li>}>
                      {(h) => (
                        <li class="flex items-center gap-2 text-sm">
                          <span class="inline-block size-2.5 rounded-full shrink-0" style={{ background: slotColor(h.slot) }} />
                          <span class="flex-1 truncate">{h.name}</span>
                          <Show when={h.estimated}><span class="badge badge-xs badge-warning badge-outline">est.</span></Show>
                          <span class="tabular-nums">{formatInt(h.damage)}</span>
                          <span class="text-xs text-base-content/50 w-12 text-right">{pct(h.share)}</span>
                        </li>
                      )}
                    </For>
                  </ul>
                </div>
              </section>
            )}
          </Show>
        </div>
      </div>
    </Show>
  )
}

// ---- Monsters ------------------------------------------------------------------

const monsterCol = createColumnHelper<Features, MonsterRow>()
const monsterColumns = [
  monsterCol.accessor('name', { header: 'Monster', sortFn: 'alphanumeric', cell: (info) => <span class="font-medium">{info.getValue()} <span class="text-xs text-base-content/50">{info.row.original.id}</span></span> }),
  monsterCol.accessor('maxHealth', { header: 'Max HP', sortFn: 'basic', meta: { class: 'text-right' }, cell: (info) => formatInt(info.getValue()) }),
  monsterCol.accessor('hpLost', {
    header: 'HP lost',
    sortFn: 'basic',
    meta: { class: 'w-52' },
    cell: (info) => (
      <div class="flex items-center gap-2">
        <progress class="progress progress-error w-24" value={info.getValue()} max={info.row.original.maxHealth || 1} />
        <span class="text-xs">{formatInt(info.getValue())} ({pct(info.row.original.maxHealth ? info.getValue() / info.row.original.maxHealth : 0, 0)})</span>
      </div>
    ),
  }),
  monsterCol.accessor('yourDamage', { header: 'Your damage', sortFn: 'basic', meta: { class: 'text-right' }, cell: (info) => formatInt(info.getValue()) }),
  monsterCol.accessor('yourHits', { header: 'Your hits', sortFn: 'basic', meta: { class: 'text-right' } }),
  monsterCol.accessor('firstSeenT', { header: 'Seen at', sortFn: 'basic', meta: { class: 'text-right' }, cell: (info) => formatDuration(info.getValue()) }),
  monsterCol.accessor('diedT', { header: 'Died at', sortFn: 'basic', meta: { class: 'text-right' }, cell: (info) => (info.getValue() == null ? <span class="opacity-40">—</span> : formatDuration(info.getValue()!)) }),
]

function Monsters(props: { log: FightLog }) {
  const rows = () => monsterBreakdown(props.log)
  const table = createSolidTable<MonsterRow>({ data: rows, columns: monsterColumns, getRowId: (m) => m.id })
  return (
    <Show when={props.log.monsters.length} fallback={<div class="text-sm text-base-content/60">No large monsters were recorded for this log.</div>}>
      <TableView table={table} />
    </Show>
  )
}

// ---- Timeline ------------------------------------------------------------------

const EVENT_KINDS = ['enrage', 'unenrage', 'death', 'cart', 'flinch', 'weapon', 'join', 'leave'] as const
const EVENT_BADGE: Record<string, string> = {
  death: 'badge-error',
  cart: 'badge-warning',
  enrage: 'badge-warning',
  unenrage: 'badge-ghost',
  flinch: 'badge-neutral',
  weapon: 'badge-secondary',
  join: 'badge-success',
  leave: 'badge-success badge-outline',
}

const eventCol = createColumnHelper<Features, FightLogEvent & { who: string }>()
const eventColumns = [
  eventCol.accessor('t', { header: 'Time', sortFn: 'basic', meta: { class: 'text-right w-20' }, cell: (info) => formatDuration(info.getValue()) }),
  eventCol.accessor('type', { header: 'Event', sortFn: 'alphanumeric', cell: (info) => <span class={['badge badge-sm', EVENT_BADGE[info.getValue()] ?? 'badge-ghost']}>{info.getValue()}</span> }),
  eventCol.accessor('who', { header: 'Who / what', sortFn: 'alphanumeric' }),
  eventCol.accessor('detail', {
    header: 'Detail',
    sortFn: 'alphanumeric',
    cell: (info) => {
      const detail = info.getValue()
      if (!detail) return ''
      return info.row.original.type === 'weapon' ? weaponDisplayName(detail) : detail
    },
  }),
]

function Timeline(props: { log: FightLog }) {
  const [enabled, setEnabled] = createSignal<Set<string>>(new Set(EVENT_KINDS.filter((k) => k !== 'flinch')))
  const toggle = (k: string) =>
    setEnabled((prev) => {
      const next = new Set(prev)
      next.has(k) ? next.delete(k) : next.add(k)
      return next
    })
  const monsterNames = () => new Map(props.log.monsters.map((m) => [m.id, m.name]))
  const playerNames = () => new Map(props.log.players.map((p) => [p.slot, p.name]))
  const rows = createMemo(() =>
    props.log.events
      .filter((e) => enabled().has(e.type))
      .map((e) => ({
        ...e,
        who: e.monster ? monsterNames().get(e.monster) ?? e.monster : e.slot != null ? playerNames().get(e.slot) ?? `slot ${e.slot + 1}` : '',
      })),
  )
  const table = createSolidTable<FightLogEvent & { who: string }>({ data: rows, columns: eventColumns, initialSorting: [{ id: 't', desc: false }] })
  const counts = createMemo(() => {
    const c: Record<string, number> = {}
    for (const e of props.log.events) c[e.type] = (c[e.type] ?? 0) + 1
    return c
  })
  return (
    <div class="flex flex-col gap-3">
      <div class="flex flex-wrap gap-2">
        <For each={EVENT_KINDS}>
          {(k) => (
            <button class={['btn btn-xs', enabled().has(k) ? 'btn-active' : 'btn-ghost']} onClick={() => toggle(k)}>
              {k} <span class="opacity-60">{counts()[k] ?? 0}</span>
            </button>
          )}
        </For>
      </div>
      <TableView table={table} empty="No events of the selected kinds." />
    </div>
  )
}

// ---- Raw -----------------------------------------------------------------------

function Raw(props: { log: FightLog }) {
  const summary = () => {
    const { hits, samples, events, ...rest } = props.log
    return JSON.stringify({ ...rest, hits: `${hits.length} hits`, samples: `${samples.length} samples`, events: `${events.length} events` }, null, 2)
  }
  const download = () => {
    const blob = new Blob([JSON.stringify(props.log, null, 1)], { type: 'application/json' })
    const a = document.createElement('a')
    a.href = URL.createObjectURL(blob)
    a.download = `${props.log.questName.replace(/[^\w-]+/g, '_')}_${props.log.startedAt.slice(0, 10)}.json`
    a.click()
    URL.revokeObjectURL(a.href)
  }
  return (
    <div class="flex flex-col gap-2">
      <div class="flex gap-2 items-center text-sm text-base-content/60">
        Header fields (arrays collapsed).
        <button class="btn btn-xs" onClick={download}>Download full JSON</button>
      </div>
      <pre class="bg-base-200 rounded-box p-4 text-xs overflow-x-auto">{summary()}</pre>
    </div>
  )
}
