import { createMemo, createSignal, For, Show } from 'solid-js'
import type { FightLog } from '../schema/fightlog'
import { availableGaugeMetrics, gaugeHistoryPoints, isolatedGaugePoints } from '../data/gaugeHistory'
import { gaugeHistoryChart } from '../charts/definitions'
import { Chart } from '../lib/Chart'

/** Experimental retrospective read-only samples, not per-hit buildup attribution. */
export function GaugeHistory(props: { log: FightLog }) {
  const monsters = createMemo(() => props.log.monsters.filter(m => props.log.gaugeSamples.some(s => s.monster === m.id)))
  const [requestedMonster, setMonster] = createSignal<string>()
  const monster = () => monsters().find(m => m.id === requestedMonster())?.id ?? monsters()[0]?.id
  const selectedMonsterName = () => monsters().find(m => m.id === monster())?.name ?? monster() ?? 'Unknown monster'
  const options = createMemo(() => availableGaugeMetrics(props.log.gaugeSamples, monster() ?? ''))
  const [requestedMetric, setMetric] = createSignal<string>()
  const activeMetric = () => options().find(o => o.key === requestedMetric()) ?? options()[0]
  const points = createMemo(() => gaugeHistoryPoints(props.log.gaugeSamples, monster() ?? '', activeMetric()?.key ?? ''))
  const last = () => points().at(-1)
  const hasTrend = () => points().some((p, i) => i > 0 && points()[i - 1]?.segment === p.segment)
  const isolated = createMemo(() => isolatedGaugePoints(points()))

  return (
    <section class="card bg-base-200">
      <div class="card-body p-4 gap-3">
        <h3 class="card-title text-base">Experimental monster state history</h3>
        <p class="text-xs text-warning">Local, unverified memory observations — not host-authoritative. Gauge changes are not per-hit part damage, KO contribution, or exact proc/break events.</p>
        <Show when={monsters().length} fallback={<p class="text-sm text-base-content/60">No monster gauge samples in this log (normal for older logs or disabled capture).</p>}>
          <div class="flex flex-wrap items-center gap-2 text-sm">
            <label for="gauge-history-monster" class="text-base-content/70">Monster</label>
            <select id="gauge-history-monster" class="select select-bordered select-sm" value={monster()} onChange={e => { setMonster(e.currentTarget.value); setMetric(undefined) }}>
              <For each={monsters()}>{m => <option value={m.id}>{m.name} ({m.id})</option>}</For>
            </select>
          </div>
          <Show when={options().length} fallback={<p class="text-sm text-base-content/60">Samples for this monster contain no usable numeric gauge readings.</p>}>
            <div role="group" aria-label="Gauge metric" class="flex flex-wrap gap-1">
              <For each={options()}>{metric => (
                <button type="button" class={['btn btn-xs', activeMetric()?.key === metric.key ? 'btn-primary' : 'btn-ghost']}
                  aria-pressed={activeMetric()?.key === metric.key ? 'true' : 'false'}
                  onClick={() => setMetric(metric.key)}>{metric.label}</button>
              )}</For>
            </div>
            <div class="text-xs text-base-content/60">
              Percent of sampled maximum; {activeMetric()?.label}. Each missing reading or capture-clock discontinuity breaks the line.
              {last() ? ` Last observed: ${last()!.value.toFixed(1)}% at ${last()!.t.toFixed(1)}s.` : ''}
            </div>
            <Show when={hasTrend()} fallback={<p class="text-sm text-base-content/60">Only isolated observations. A trend requires two consecutive valid samples.</p>}>
              <Chart definition={gaugeHistoryChart(points(), activeMetric()?.label ?? 'Gauge')} height={250}
                ariaLabel={`Observed local gauge history for ${selectedMonsterName()} (${monster()}): ${activeMetric()?.label ?? 'Gauge'} (local-unverified)`} />
            </Show>
            <Show when={isolated().length > 0}>
              <div class="text-xs text-base-content/70">
                <p>{isolated().length} isolated valid reading(s) (not joined by a line; latest five):</p>
                <ul class="list-disc pl-5" aria-label="Isolated gauge observations">
                  <For each={isolated().slice(-5).reverse()}>{p => <li>{p.value.toFixed(1)}% at {p.t.toFixed(1)}s</li>}</For>
                </ul>
              </div>
            </Show>
            <Show when={props.log.gaugeSamplesDropped > 0 || props.log.gaugeClockDiscontinuities > 0}>
              <p class="text-xs text-warning">Recording gaps or adjusted timestamps are present. Compare captured gauge time to DPS or hit timestamps only with caution.</p>
            </Show>
          </Show>
        </Show>
      </div>
    </section>
  )
}
