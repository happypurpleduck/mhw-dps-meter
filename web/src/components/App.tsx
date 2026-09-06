import { Loading, Show, Switch, Match, createSignal, onSettled } from 'solid-js'
import { logStore } from '../data/store'
import { Navbar } from './Navbar'
import { HuntList } from './HuntList'
import { HuntDetail } from './HuntDetail'
import { TrialsView } from './TrialsView'
import { LoadPanel } from './LoadPanel'

const SIDEBAR_KEY = 'mhw-viewer-sidebar-width'
const SIDEBAR_MIN = 320
const SIDEBAR_MAX = 960

function initialSidebarWidth(): number {
  try {
    const stored = Number(localStorage.getItem(SIDEBAR_KEY))
    if (stored >= SIDEBAR_MIN && stored <= SIDEBAR_MAX) return stored
  } catch {
    // storage blocked
  }
  return 512
}

export function App() {
  const [sidebarWidth, setSidebarWidth] = createSignal(initialSidebarWidth())
  const [dragging, setDragging] = createSignal(false)

  /** Drag the separator between the hunt list and the detail pane; width persists per browser. */
  const startResize = (e: PointerEvent) => {
    e.preventDefault()
    const startX = e.clientX
    const startWidth = sidebarWidth()
    setDragging(true)
    const move = (ev: PointerEvent) => setSidebarWidth(Math.min(SIDEBAR_MAX, Math.max(SIDEBAR_MIN, startWidth + ev.clientX - startX)))
    const stop = () => {
      window.removeEventListener('pointermove', move)
      window.removeEventListener('pointerup', stop)
      setDragging(false)
      try {
        localStorage.setItem(SIDEBAR_KEY, String(sidebarWidth()))
      } catch {
        // storage blocked
      }
    }
    window.addEventListener('pointermove', move)
    window.addEventListener('pointerup', stop)
  }

  onSettled(() => {
    // Defer: restore() writes store signals, which Solid 2 rejects inside an owned scope.
    queueMicrotask(() => void logStore.restore())
    const prevent = (e: DragEvent) => e.preventDefault()
    const drop = (e: DragEvent) => {
      e.preventDefault()
      const files = e.dataTransfer?.files
      if (files && files.length) void logStore.loadFiles(files)
    }
    window.addEventListener('dragover', prevent)
    window.addEventListener('drop', drop)
    return () => {
      window.removeEventListener('dragover', prevent)
      window.removeEventListener('drop', drop)
    }
  })

  return (
    <div class="min-h-screen bg-base-100 text-base-content flex flex-col">
      <Navbar />
      <Show when={logStore.error()}>
        {(message) => (
          <div role="alert" class="alert alert-error m-3 whitespace-pre-wrap text-sm">
            {message()}
          </div>
        )}
      </Show>
      <Show when={logStore.source()} fallback={<LoadPanel />}>
        <div class={['flex flex-1 flex-col lg:flex-row min-h-0', { 'select-none cursor-col-resize': dragging() }]}>
          <aside
            class="border-b lg:border-b-0 border-base-300 flex flex-col min-h-0 lg:shrink-0 lg:w-(--sidebar-w)"
            style={{ '--sidebar-w': `${sidebarWidth()}px` }}
          >
            <HuntList />
          </aside>
          <div
            role="separator"
            aria-orientation="vertical"
            title="Drag to resize"
            class={['hidden lg:block w-1.5 shrink-0 cursor-col-resize bg-base-300 hover:bg-primary/60 transition-colors', { 'bg-primary/60': dragging() }]}
            onPointerDown={startResize}
          />
          <main class="flex-1 min-w-0 p-4 overflow-x-hidden">
            <Switch>
              <Match when={logStore.view().kind === 'trials'}>
                <TrialsView />
              </Match>
              <Match when={logStore.view().kind === 'hunt'}>
                <Loading fallback={<div class="flex justify-center p-12"><span class="loading loading-spinner loading-lg" /></div>}>
                  <HuntDetail />
                </Loading>
              </Match>
              <Match when={true}>
                <div class="p-8 text-base-content/60">Pick a hunt on the left.</div>
              </Match>
            </Switch>
          </main>
        </div>
      </Show>
    </div>
  )
}
