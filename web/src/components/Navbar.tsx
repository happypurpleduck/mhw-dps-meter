import { Show, createSignal, onSettled } from 'solid-js'
import { logStore } from '../data/store'

const THEME_KEY = 'mhw-viewer-theme'

function initialDark(): boolean {
  try {
    const stored = localStorage.getItem(THEME_KEY)
    if (stored) return stored === 'dark'
  } catch {
    // storage blocked
  }
  return window.matchMedia('(prefers-color-scheme: dark)').matches
}

function ThemeToggle() {
  // Plain (non-reactive) initial read; Solid 2 forbids signal writes inside lifecycle scopes.
  const [dark, setDark] = createSignal(initialDark())
  const applyDom = (isDark: boolean) => {
    document.documentElement.dataset.theme = isDark ? 'dark' : 'light'
    try {
      localStorage.setItem(THEME_KEY, isDark ? 'dark' : 'light')
    } catch {
      // storage blocked
    }
  }
  onSettled(() => applyDom(dark()))
  const toggle = (isDark: boolean) => {
    setDark(isDark)
    applyDom(isDark)
  }
  return (
    <label class="swap swap-rotate btn btn-ghost btn-circle" title="Toggle theme">
      <input type="checkbox" checked={dark()} onChange={(e) => toggle(e.currentTarget.checked)} />
      <span class="swap-on">🌙</span>
      <span class="swap-off">☀️</span>
    </label>
  )
}

export function Navbar() {
  let fileInput!: HTMLInputElement
  return (
    <div class="navbar bg-base-200 border-b border-base-300 px-4 gap-2">
      <div class="flex-1 flex items-center gap-3">
        <span class="text-lg font-semibold tracking-tight">⚔️ MHW Fight Logs</span>
        <Show when={logStore.source()}>
          {(s) => <span class="badge badge-outline badge-sm">{s().label} · {logStore.entries().length} hunts</span>}
        </Show>
        <Show when={logStore.busy()}>
          <span class="loading loading-spinner loading-xs" />
        </Show>
      </div>
      <div class="flex items-center gap-1">
        <Show when={logStore.source()}>
          <button
            class={['btn btn-sm btn-ghost', { 'btn-active': logStore.view().kind === 'trials' }]}
            onClick={() => logStore.showTrials()}
          >
            Time trials
          </button>
        </Show>
        <Show when={logStore.supportsDirectoryPicker}>
          <button class="btn btn-sm btn-primary" onClick={() => void logStore.pickDirectory()}>
            Open logs folder
          </button>
        </Show>
        <button class="btn btn-sm" onClick={() => fileInput.click()}>
          Open files
        </button>
        <input
          ref={fileInput}
          type="file"
          accept=".json,application/json"
          multiple
          class="hidden"
          onChange={(e) => {
            const files = e.currentTarget.files
            if (files?.length) void logStore.loadFiles(files)
            e.currentTarget.value = ''
          }}
        />
        <button class="btn btn-sm btn-ghost" onClick={() => void logStore.loadSample()}>
          Sample
        </button>
        <ThemeToggle />
      </div>
    </div>
  )
}
