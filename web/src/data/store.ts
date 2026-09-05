import { createMemo, createSignal } from 'solid-js'
import type { FightLog, IndexEntry } from '../schema/fightlog'
import { kvDelete, kvGet, kvSet } from './idb'
import { directorySource, filesSource, sampleSource, supportsDirectoryPicker, urlSource, type LogSource } from './sources'

const DIR_KEY = 'logs-directory'

export type View = { kind: 'hunt'; file: string } | { kind: 'trials' } | { kind: 'empty' }

const [source, setSource] = createSignal<LogSource | undefined>(undefined)
const [view, setView] = createSignal<View>({ kind: 'empty' })
const [busy, setBusy] = createSignal(false)
const [error, setError] = createSignal<string | undefined>(undefined)
const [rememberedDir, setRememberedDir] = createSignal<FileSystemDirectoryHandle | undefined>(undefined)

const entries = createMemo<IndexEntry[]>(() => source()?.entries ?? [])

/**
 * The selected hunt's log. Returning a promise from createMemo is Solid 2's
 * async model: readers inside a <Loading> boundary wait for it.
 */
const selectedLog = createMemo<Promise<FightLog | undefined>>(async () => {
  const v = view()
  const s = source()
  if (v.kind !== 'hunt' || !s) return undefined
  return s.loadLog(v.file)
})

async function run(task: () => Promise<LogSource>) {
  setBusy(true)
  setError(undefined)
  try {
    const next = await task()
    setSource(next)
    const first = next.entries[0]
    setView(first ? { kind: 'hunt', file: first.file } : { kind: 'empty' })
  } catch (err) {
    setError((err as Error).message)
  } finally {
    setBusy(false)
  }
}

type PickerWindow = Window & { showDirectoryPicker(o?: object): Promise<FileSystemDirectoryHandle> }
type PermHandle = FileSystemDirectoryHandle & {
  queryPermission(o: { mode: string }): Promise<string>
  requestPermission(o: { mode: string }): Promise<string>
}

export const logStore = {
  source,
  entries,
  view,
  setView,
  selectedLog,
  busy,
  error,
  rememberedDir,
  supportsDirectoryPicker,

  selectHunt(file: string) {
    setView({ kind: 'hunt', file })
  },

  showTrials() {
    setView({ kind: 'trials' })
  },

  loadFiles(files: Iterable<File>) {
    return run(() => filesSource(files))
  },

  loadSample() {
    return run(sampleSource)
  },

  /** `?logs=<url>` loads any served folder that contains an index.json. */
  loadUrl(url: string) {
    return run(() => urlSource(url, url))
  },

  async pickDirectory() {
    if (!supportsDirectoryPicker) return
    let handle: FileSystemDirectoryHandle
    try {
      handle = await (window as unknown as PickerWindow).showDirectoryPicker({ id: 'mhw-logs', mode: 'read' })
    } catch {
      return // user cancelled
    }
    await kvSet(DIR_KEY, handle)
    setRememberedDir(handle)
    return run(() => directorySource(handle))
  },

  /** Re-open the folder picked in an earlier session (needs a click for the permission prompt). */
  async reconnectDirectory() {
    const handle = rememberedDir()
    if (!handle) return
    const perm = await (handle as PermHandle).requestPermission({ mode: 'read' })
    if (perm !== 'granted') {
      setError('Read permission for the folder was not granted.')
      return
    }
    return run(() => directorySource(handle))
  },

  async forgetDirectory() {
    await kvDelete(DIR_KEY)
    setRememberedDir(undefined)
  },

  /** Startup: `?logs=` URL wins; otherwise restore the remembered folder if permission is still granted. */
  async restore() {
    const params = new URLSearchParams(location.search)
    const url = params.get('logs')
    if (url) {
      await run(() => urlSource(url, url))
      // Deep links: ?view=trials or ?hunt=<file> (plus ?tab= handled by the detail view).
      const hunt = params.get('hunt')
      if (params.get('view') === 'trials') setView({ kind: 'trials' })
      else if (hunt && entries().some((e) => e.file === hunt)) setView({ kind: 'hunt', file: hunt })
      return
    }
    if (!supportsDirectoryPicker) return
    const handle = await kvGet<FileSystemDirectoryHandle>(DIR_KEY)
    if (!handle) return
    setRememberedDir(handle)
    try {
      const perm = await (handle as PermHandle).queryPermission({ mode: 'read' })
      if (perm === 'granted') await run(() => directorySource(handle))
    } catch {
      // handle no longer valid
    }
  },
}
