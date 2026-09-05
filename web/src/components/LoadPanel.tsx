import { Show } from 'solid-js'
import { logStore } from '../data/store'

export function LoadPanel() {
  return (
    <div class="flex-1 flex items-center justify-center p-8">
      <div class="card bg-base-200 shadow-xl max-w-2xl w-full">
        <div class="card-body gap-4">
          <h2 class="card-title text-2xl">Load your fight logs</h2>
          <p class="text-base-content/70">
            The MHW DPS Meter plugin writes one JSON file per hunt plus an <code class="kbd kbd-sm">index.json</code> into
            <code class="kbd kbd-sm ml-1">nativePC/plugins/CSharp/MhwDpsMeter/logs/</code>. Point this page at that folder and it
            stays read-only on your disk; nothing is uploaded.
          </p>
          <ul class="steps steps-vertical lg:steps-horizontal text-sm">
            <li class="step step-primary">Pick the folder or drop files</li>
            <li class="step">Browse hunts and trials</li>
            <li class="step">Compare moves and runs</li>
          </ul>
          <div class="flex flex-wrap gap-2">
            <Show when={logStore.supportsDirectoryPicker} fallback={<span class="text-sm text-base-content/60">Folder picking needs a Chromium browser; use "Open files" or drag and drop instead.</span>}>
              <button class="btn btn-primary" onClick={() => void logStore.pickDirectory()}>
                Open logs folder
              </button>
              <Show when={logStore.rememberedDir()}>
                {(dir) => (
                  <button class="btn" onClick={() => void logStore.reconnectDirectory()}>
                    Reconnect “{dir().name}”
                  </button>
                )}
              </Show>
            </Show>
            <button class="btn btn-ghost" onClick={() => void logStore.loadSample()}>
              Try the sample data
            </button>
          </div>
          <div class="border-2 border-dashed border-base-300 rounded-box p-6 text-center text-base-content/60 text-sm">
            …or drag fight-log <code>.json</code> files anywhere onto this page.
          </div>
        </div>
      </div>
    </div>
  )
}
