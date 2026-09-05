/**
 * Solid 2 host for a @tanstack/charts definition.
 *
 * Uses the framework-agnostic DOM host (`mountChart`) because the package's
 * Solid adapter is written against Solid 1 (`solid-js/web`, `onMount`).
 * The effect's compute phase tracks the definition; the apply phase mounts or
 * updates the chart, which is exactly the two-phase createEffect of Solid 2.
 */
import { createEffect, onCleanup } from 'solid-js'
import { mountChart, type ChartHost, type DomChartDefinition } from '@tanstack/charts'

export interface ChartProps {
  definition: DomChartDefinition<any, any, any>
  height?: number
  ariaLabel?: string
  class?: string
}

export function Chart(props: ChartProps) {
  let el!: HTMLDivElement
  let host: ChartHost<any, any, any> | undefined

  createEffect(
    () => ({ definition: props.definition, height: props.height ?? 300, ariaLabel: props.ariaLabel ?? 'chart' }),
    (options) => {
      const width = el.clientWidth || 640
      const merged = { ...options, initialWidth: width, width: undefined }
      if (!host) host = mountChart(el, merged)
      else host.update(merged)
    },
  )

  onCleanup(() => host?.destroy())

  return <div ref={el} class={['chart-host w-full', props.class]} />
}
