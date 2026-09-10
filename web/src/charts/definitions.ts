import { defineChart, lineY, barX, ruleX, type DomChartDefinition } from '@tanstack/charts'
import { scaleLinear } from '@tanstack/charts/scales/linear'
import { scaleBand } from '@tanstack/charts/scales/band'
import { tooltip } from '@tanstack/charts/tooltip'
import type { FightLog } from '../schema/fightlog'
import { slotColor } from '../schema/fightlog'
import { damageCurves, rollingDps, intervalDps, type CurvePoint, type RatePoint } from '../data/analysis'
import { formatInt } from '../data/analysis'

type Def = DomChartDefinition<any, any, any>

export interface EventMarker {
  t: number
  label: string
  kind: 'death' | 'enrage' | 'unenrage' | 'join' | 'leave' | 'weapon' | 'cart'
}

const MARKER_COLORS: Record<EventMarker['kind'], string> = {
  death: '#ef4444',
  enrage: '#f97316',
  unenrage: '#a3a3a3',
  join: '#22c55e',
  leave: '#22c55e',
  weapon: '#a855f7',
  cart: '#eab308',
}

export function eventMarkers(log: FightLog, kinds: EventMarker['kind'][] = ['death', 'enrage', 'cart']): EventMarker[] {
  const names = new Map(log.monsters.map((m) => [m.id, m.name]))
  return log.events
    .filter((e) => (kinds as string[]).includes(e.type))
    .map((e) => ({
      t: e.t,
      kind: e.type as EventMarker['kind'],
      label: `${e.type}${e.monster ? ` ${names.get(e.monster) ?? e.monster}` : ''}${e.detail && e.type !== 'flinch' ? ` ${e.detail}` : ''}`,
    }))
}

/** Cumulative damage per hunter with monster death/enrage markers. */
export function damageCurveChart(log: FightLog, markers: EventMarker[] = eventMarkers(log)): Def {
  const rows = damageCurves(log)
  return defineChart({
    marks: [
      lineY(rows, {
        id: 'damage',
        x: 't',
        y: 'damage',
        z: 'player',
        stroke: (d: CurvePoint) => slotColor(d.slot),
        strokeWidth: 2,
      }),
      ruleX(markers, {
        id: 'events',
        x: 't',
        stroke: (m: EventMarker) => MARKER_COLORS[m.kind],
        strokeDasharray: '4 3',
        strokeOpacity: 0.8,
      }),
    ],
    scales: {
      x: { scale: scaleLinear, nice: true, axis: { label: 'Hunt time (s)' } },
      y: { scale: scaleLinear, nice: true, grid: true, axis: { label: 'Cumulative damage' } },
    },
    tooltip: {
      use: tooltip,
      format: (point: { datum: CurvePoint }) => `${point.datum.player}: ${formatInt(point.datum.damage)} @ ${point.datum.t}s`,
    },
  } as never) as unknown as Def
}

/** Rolling DPS per hunter. */
export function dpsCurveChart(log: FightLog, windowSeconds: number | null = 20): Def {
  const rows = windowSeconds === null ? intervalDps(log) : rollingDps(log, windowSeconds)
  return defineChart({
    marks: [
      lineY(rows, {
        id: 'dps',
        x: 't',
        y: 'dps',
        z: 'player',
        stroke: (d: RatePoint) => slotColor(d.slot),
        strokeWidth: 1.5,
      }),
    ],
    scales: {
      x: { scale: scaleLinear, nice: true, axis: { label: 'Hunt time (s)' } },
      y: { scale: scaleLinear, nice: true, grid: true, axis: { label: windowSeconds === null ? 'DPS (per sample interval)' : `DPS (${windowSeconds}s window)` } },
    },
    tooltip: {
      use: tooltip,
      format: (point: { datum: RatePoint }) => `${point.datum.player}: ${point.datum.dps.toFixed(1)} DPS @ ${point.datum.t}s`,
    },
  } as never) as unknown as Def
}

/** Horizontal bars: damage per move, biggest first. */
export function movesBarChart(moves: { name: string; damage: number; hits: number }[], color = '#52b8ff'): Def {
  const rows = moves.slice(0, 15)
  return defineChart({
    marks: [
      barX(rows, {
        id: 'moves',
        y: 'name',
        x: 'damage',
        fill: color,
        radius: 3,
        inset: 2,
      }),
    ],
    scales: {
      y: { scale: () => scaleBand<string>().padding(0.2), axis: { label: undefined } },
      x: { scale: scaleLinear, nice: true, grid: true, axis: { label: 'Damage' } },
    },
    tooltip: {
      use: tooltip,
      format: (point: { datum: { name: string; damage: number; hits: number } }) =>
        `${point.datum.name}: ${formatInt(point.datum.damage)} (${point.datum.hits} hits)`,
    },
  } as never) as unknown as Def
}

/** Two or more cumulative curves on one axis (trial comparison). */
export function compareCurvesChart(series: { label: string; color: string; points: CurvePoint[] }[]): Def {
  const rows = series.flatMap((s) => s.points.map((p) => ({ ...p, player: s.label, color: s.color })))
  return defineChart({
    marks: [
      lineY(rows, {
        id: 'compare',
        x: 't',
        y: 'damage',
        z: 'player',
        stroke: (d: { color: string }) => d.color,
        strokeWidth: 2,
      }),
    ],
    scales: {
      x: { scale: scaleLinear, nice: true, axis: { label: 'Seconds into trial' } },
      y: { scale: scaleLinear, nice: true, grid: true, axis: { label: 'Cumulative damage' } },
    },
    tooltip: {
      use: tooltip,
      format: (point: { datum: CurvePoint }) => `${point.datum.player}: ${formatInt(point.datum.damage)} @ ${point.datum.t}s`,
    },
  } as never) as unknown as Def
}
