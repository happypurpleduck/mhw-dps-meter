import { For, Show, createMemo, createSignal } from 'solid-js'
import { createColumnHelper } from '@tanstack/table-core'
import type { FightLog } from '../schema/fightlog'
import { slotColor } from '../schema/fightlog'
import { formatDuration, formatInt, hitTimeline, partBreakdown, partHits, pct, type PartHunterRow, type PartRow } from '../data/analysis'
import { createSolidTable, type Features } from '../lib/table'
import { Chart } from '../lib/Chart'
import { partTimelineChart } from '../charts/definitions'
import { StatTile, TableView } from './TableView'

const partCol = createColumnHelper<Features, PartRow>()
const partColumns = [
  partCol.accessor('name', { header: 'Part', sortFn: 'alphanumeric' }),
  partCol.accessor('damage', { header: 'Damage', sortFn: 'basic', cell: (i) => formatInt(i.getValue()) }),
  partCol.accessor('share', { header: 'Share', sortFn: 'basic', cell: (i) => pct(i.getValue()) }),
  partCol.accessor('dps', { header: 'Hunt DPS', sortFn: 'basic', cell: (i) => i.getValue().toFixed(1) }),
  partCol.accessor('hits', { header: 'Hits / rows', sortFn: 'basic' }),
  partCol.accessor('avg', { header: 'Average', sortFn: 'basic', cell: (i) => i.getValue().toFixed(1) }),
  partCol.accessor('max', { header: 'Largest', sortFn: 'basic', cell: (i) => formatInt(i.getValue()) }),
]
const hunterCol = createColumnHelper<Features, PartHunterRow>()
const hunterColumns = [
  hunterCol.accessor('name', { header: 'Hunter', sortFn: 'alphanumeric', cell: (i) => (
    <span class="flex items-center gap-2">
      <span class="inline-block size-2.5 rounded-full shrink-0" style={{ background: slotColor(i.row.original.slot) }} />
      {i.getValue()}
      <Show when={i.row.original.estimated}><span class="badge badge-xs badge-warning badge-outline">est.</span></Show>
    </span>
  ) }),
  hunterCol.accessor('damage', { header: 'Damage', sortFn: 'basic', cell: (i) => formatInt(i.getValue()) }),
  hunterCol.accessor('share', { header: 'Part share', sortFn: 'basic', cell: (i) => pct(i.getValue()) }),
  hunterCol.accessor('dps', { header: 'Hunt DPS', sortFn: 'basic', cell: (i) => i.getValue().toFixed(1) }),
  hunterCol.accessor('hits', { header: 'Hits / rows', sortFn: 'basic' }),
  hunterCol.accessor('avg', { header: 'Average', sortFn: 'basic', cell: (i) => i.getValue().toFixed(1) }),
  hunterCol.accessor('max', { header: 'Largest', sortFn: 'basic', cell: (i) => formatInt(i.getValue()) }),
  hunterCol.accessor('critRate', { header: 'Crit', sortFn: 'basic', cell: (i) => i.getValue() == null ? '—' : pct(i.getValue()!) }),
  hunterCol.accessor('tenderizedRate', { header: 'Tenderized', sortFn: 'basic', cell: (i) => i.getValue() == null ? '—' : pct(i.getValue()!) }),
]

export function Parts(props: { log: FightLog }) {
  const [monsterChoice, setMonsterChoice] = createSignal<{ hunt: string; id: string | null }>()
  const [partChoice, setPartChoice] = createSignal<{ scope: string; part: number | null }>()
  const [windowSeconds, setWindowSeconds] = createSignal(20)
  const unassigned = createMemo(() => partBreakdown(props.log, null)[0])
  const monsterId = () => {
    const choice = monsterChoice()
    if (choice?.hunt === props.log.startedAt && (choice.id == null ? unassigned() : props.log.monsters.some((m) => m.id === choice.id))) return choice.id
    return props.log.monsters.find((m) => props.log.hits.some((h) => h.monster === m.id))?.id
      ?? (unassigned() ? null : props.log.monsters[0]?.id ?? null)
  }
  const monster = () => props.log.monsters.find((m) => m.id === monsterId())
  const chooseMonster = (id: string | null) => setMonsterChoice({ hunt: props.log.startedAt, id })
  const scope = () => `${props.log.startedAt}/${monsterId()}`
  const rows = createMemo(() => monsterId() == null ? (unassigned() ? [unassigned()!] : []) : partBreakdown(props.log, monsterId()))
  const selected = createMemo(() => (partChoice()?.scope === scope() ? rows().find((r) => r.part === partChoice()?.part) : undefined)
    ?? rows().find((r) => r.part != null) ?? rows()[0])
  const choosePart = (part: number | null) => setPartChoice({ scope: scope(), part })
  const timeline = createMemo(() => hitTimeline(props.log, selected() ? partHits(props.log, monsterId(), selected()!.part) : [], windowSeconds()))
  const coverage = () => {
    const total = rows().reduce((s, r) => s + r.damage, 0)
    return total ? rows().filter((r) => r.part != null).reduce((s, r) => s + r.damage, 0) / total : 0
  }
  const table = createSolidTable<PartRow>({ data: rows, columns: partColumns,
    getRowId: (r) => String(r.part ?? 'unknown'), initialSorting: [{ id: 'damage', desc: true }] })
  const hunters = createSolidTable<PartHunterRow>({ data: () => selected()?.hunters ?? [], columns: hunterColumns,
    getRowId: (r) => String(r.slot), initialSorting: [{ id: 'damage', desc: true }] })

  return (
    <div class="flex flex-col gap-4">
      <Show when={props.log.hits.some((h) => h.estimated)}>
        <p class="text-sm text-base-content/70">Teammate damage was recorded as estimated totals without part tags. Known-part charts only include hunters with exact tagged hits.</p>
      </Show>
      <Show when={unassigned()}>{(unknown) => (
        <div class="rounded-box border border-base-300 p-3 flex flex-wrap items-center gap-3 text-sm">
          <span class="flex-1 min-w-56">{formatInt(unknown().damage)} recorded damage from {unknown().hunters.map((h) => h.name).join(', ')} has no monster or part attribution.</span>
          <button class="btn btn-sm btn-outline" onClick={() => chooseMonster(null)}>View unassigned damage</button>
        </div>
      )}</Show>
      <div class="flex flex-wrap gap-3 items-end">
        <label class="flex flex-col gap-1 text-sm">Monster
          <select class="select select-bordered" aria-label="Monster for part breakdown" value={monsterId() ?? ''}
            onChange={(e) => chooseMonster(e.currentTarget.value || null)}>
            <For each={props.log.monsters}>{(m) => <option value={m.id}>{m.name} · {m.id}</option>}</For>
            <Show when={unassigned()}><option value="">Unassigned damage · Unknown monster</option></Show>
          </select>
        </label>
        <label class="flex flex-col gap-1 text-sm">Part
          <select class="select select-bordered" aria-label="Monster part" value={String(selected()?.part ?? 'unknown')}
            disabled={!rows().length} onChange={(e) => choosePart(e.currentTarget.value === 'unknown' ? null : Number(e.currentTarget.value))}>
            <For each={rows()}>{(r) => <option value={String(r.part ?? 'unknown')}>{r.name}</option>}</For>
          </select>
        </label>
        <span class="text-sm text-base-content/60 pb-2">{monsterId() == null ? 'Monster and part not recorded' : `${pct(coverage())} of this monster’s recorded damage has a known part`}</span>
      </div>
      <p class="text-sm text-base-content/70">Select a part to inspect its hunters and damage over time. Unknown part includes untagged damage assigned to the selected monster. Damage without a monster is listed separately under Unassigned damage.</p>
      <Show when={monsterId() == null && rows().length}>
        <p class="text-sm text-base-content/60">These rows cannot be assigned to a monster or part. The charts below show the available damage per hunter; they may cover several monsters.</p>
      </Show>
      <Show when={monsterId() != null && rows().length > 0 && !rows().some((r) => r.part != null)}>
        <p class="text-sm text-base-content/60">No part tags recorded for this monster. Unknown part still shows the available damage and timeline.</p>
      </Show>
      <Show when={rows().length} fallback={<p class="text-sm text-base-content/60">{monsterId() == null ? 'No recorded damage to display.' : 'No recorded hits on this monster.'}</p>}>
        <TableView table={table} onRowClick={(r) => choosePart(r.original.part)}
          rowClass={(r) => ({ 'bg-base-300/50': selected()?.part === r.original.part })} />
      </Show>
      <Show when={selected()}>{(part) => (
        <>
          <h3 class="text-lg font-semibold">{monster()?.name ?? 'Unassigned damage'} · {part().name}</h3>
          <div class="stats stats-vertical sm:stats-horizontal bg-base-200 rounded-box flex-wrap">
            <StatTile title={monsterId() == null ? 'Unassigned damage' : 'Part damage'} value={formatInt(part().damage)} desc={`${pct(part().share)} of recorded damage in this view`} />
            <StatTile title="Hunt DPS" value={part().dps.toFixed(1)} desc={`over ${formatDuration(props.log.durationSeconds)}`} />
            <StatTile title={part().estimated ? 'Recorded rows' : 'Hits'} value={String(part().hits)} desc={`avg ${part().avg.toFixed(1)} · largest ${formatInt(part().max)}`} />
            <StatTile title="Crit / tenderized" value={part().critRate == null ? '—' : `${pct(part().critRate!)} / ${pct(part().tenderizedRate!)}`} desc={part().estimated ? 'Unavailable for estimated rows' : 'Share of hits on this part'} />
          </div>
          <section class="flex flex-col gap-2">
            <h4 class="font-medium">Hunter contributions to {part().name}</h4>
            <p class="text-xs text-base-content/60">DPS uses the full hunt duration. Shares and charts use recorded hits. Estimated rows represent damage increments; their crit and tenderize rates are unavailable.</p>
            <TableView table={hunters} />
          </section>
          <section class="card bg-base-200"><div class="card-body p-4 gap-2">
            <h4 class="font-medium">{part().name} · Cumulative damage</h4>
            <Chart definition={partTimelineChart(timeline().damage, 'damage', 'Cumulative damage')} height={260} ariaLabel={`${part().name} cumulative damage per hunter`} />
            <div class="flex flex-wrap gap-4 text-xs"><For each={part().hunters}>{(h) => <span style={{ color: slotColor(h.slot) }}>{h.name}{h.estimated ? ' (est.)' : ''}</span>}</For></div>
          </div></section>
          <section class="card bg-base-200"><div class="card-body p-4 gap-2">
            <h4 class="font-medium">{part().name} · DPS</h4>
            <p class="text-xs text-base-content/60">Damage in each 2-second interval, divided by its duration. Idle intervals stay at zero.</p>
            <Chart definition={partTimelineChart(timeline().dps, 'dps', 'DPS (2s intervals)')} height={240} ariaLabel={`${part().name} DPS per hunter`} />
          </div></section>
          <section class="card bg-base-200"><div class="card-body p-4 gap-2">
            <div class="flex flex-wrap justify-between gap-2 items-center">
              <h4 class="font-medium">{part().name} · Rolling DPS</h4>
              <label class="flex gap-2 items-center text-sm">Window
                <select class="select select-sm" aria-label="Part DPS window" value={windowSeconds()} onChange={(e) => setWindowSeconds(Number(e.currentTarget.value))}>
                  <For each={[10, 20, 30, 60]}>{(w) => <option value={w}>{w}s</option>}</For>
                </select>
              </label>
            </div>
            <Chart definition={partTimelineChart(timeline().rolling, 'dps', `DPS (${windowSeconds()}s window)`)} height={240} ariaLabel={`${part().name} rolling DPS per hunter`} />
          </div></section>
        </>
      )}</Show>
    </div>
  )
}
