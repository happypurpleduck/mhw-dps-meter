import { Loading, Show, Switch, Match, onSettled } from 'solid-js'
import { logStore } from '../data/store'
import { Navbar } from './Navbar'
import { HuntList } from './HuntList'
import { HuntDetail } from './HuntDetail'
import { TrialsView } from './TrialsView'
import { LoadPanel } from './LoadPanel'

export function App() {
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
        <div class="flex flex-1 flex-col lg:flex-row min-h-0">
          <aside class="lg:w-[32rem] lg:min-w-[30rem] lg:max-w-[32rem] border-b lg:border-b-0 lg:border-r border-base-300 flex flex-col min-h-0">
            <HuntList />
          </aside>
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
