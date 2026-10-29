import { createMemo, createSignal, For, Show } from 'solid-js'
import type { FightLog } from '../schema/fightlog'
import { availableGaugeMetrics, gaugeHistoryPoints, isolatedGaugePoints, gaugeCoverage, gaugeQualityMarkers, gaugeQualityMarkerLabel, recentGaugeQualityMarkers } from '../data/gaugeHistory'
import { observedCounterTimeline } from '../data/gaugeCounterIntervals'
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
  const coverage = createMemo(() => gaugeCoverage(props.log.gaugeSamples, monster() ?? '', activeMetric()?.key))
  const counterTimeline = createMemo(() => observedCounterTimeline(
    props.log.gaugeSamples, monster() ?? '',
  ))
  const qualityMarkers = createMemo(() => gaugeQualityMarkers(props.log.gaugeSamples, monster() ?? ''))

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
          <div class="text-xs text-base-content/70" aria-label="Monster gauge sample quality">
            Stored samples for this monster: {coverage().observations}; invalid timestamps: {coverage().invalidTime}.
            Observed boundaries: {coverage().gapsOver6s} gaps over 6s, {coverage().sourceChanges} source changes,
            {coverage().clockBoundaries} clock boundaries; {coverage().nonDefaultSource} non-default source label(s) (authority unverified).
            These are counts of stored observations, not a percentage of hunt coverage.
          </div>
          <Show when={qualityMarkers().length > 0}>
            <div class="text-xs text-base-content/70">
              <p>Stored capture-quality markers (latest five in capture order; not combat events or precise hit times):</p>
              <ul class="list-disc pl-5" aria-label="Stored capture-quality markers">
                <For each={recentGaugeQualityMarkers(qualityMarkers())}>{marker =>
                  <li>{gaugeQualityMarkerLabel(marker)}</li>
                }</For>
              </ul>
            </div>
          </Show>
          <Show when={counterTimeline().length > 0}>
            <div class="text-xs text-base-content/70">
              <p>Recent observed counter increases across sampled ailments and parts (latest five by interval end; not exact proc/break times or hunter attribution):</p>
              <ul class="list-disc pl-5" aria-label="Combined observed gauge counter timeline">
                <For each={counterTimeline()}>{change =>
                  <li>{change.label}: +{change.increase} ({change.before} to {change.after}) observed between {change.startT.toFixed(1)}s and {change.endT.toFixed(1)}s</li>
                }</For>
              </ul>
            </div>
          </Show>
          <Show when={options().length} fallback={<p class="text-sm text-base-content/60">No usable numeric gauge readings for this monster; metric-specific quality is unavailable.</p>}>
            <div role="group" aria-label="Gauge metric" class="flex flex-wrap gap-1">
              <For each={options()}>{metric => (
                <button type="button" class={['btn btn-xs', activeMetric()?.key === metric.key ? 'btn-primary' : 'btn-ghost']}
                  aria-pressed={activeMetric()?.key === metric.key ? 'true' : 'false'}
                  onClick={() => setMetric(metric.key)}>{metric.label}</button>
              )}</For>
            </div>
            <div class="text-xs text-base-content/70" aria-label="Selected gauge metric quality">
              Selected metric usable: {coverage().usable}; metric missing/invalid: {coverage().missingMetric};
              out-of-order readings: {coverage().outOfOrder}.
            </div>
            <div class="text-xs text-base-content/60">
              Percent of sampled maximum; {activeMetric()?.label}. Missing readings, capture-clock discontinuities, changes of observation source and gaps over 6s break the line.
              {last() ? ` Last observed: ${last()!.value.toFixed(1)}% at ${last()!.t.toFixed(1)}s.` : ''}
              {activeMetric()?.key.startsWith('ailment-duration:') ? ' Duration is a sampled remaining-time fraction, not a precise status start/end event.' : ''}
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
          </Show>
        </Show>
        <Show when={props.log.gaugeSamplesDropped > 0 || props.log.gaugeClockDiscontinuities > 0}>
          <p class="text-xs text-warning">
            Hunt-wide quality: {props.log.gaugeSamplesDropped} due observation interval(s) omitted by recording cap;
            {props.log.gaugeClockDiscontinuities} clock correction(s). These totals cannot be assigned to a monster.
            Gauge timestamps must not be treated as exact hit or DPS event times.
          </p>
        </Show>
      </div>
    </section>
  )
}
